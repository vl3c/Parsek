#!/usr/bin/env python3
"""The semantic type graph next to the text scan, and why the two disagree.

`ilgraph.py` reads the compiled assembly; this module joins that to the text
model `archview.build_model` returns: it builds the semantic type graph over
the text model's own nodes (same names, same modules), measures the knot and
the cut residue on it, and classifies every edge the two graphs disagree on.

Report:

    python3 scripts/arch/semantic.py --il Source/Parsek/bin/Debug/Parsek.dll
    python3 scripts/arch/semantic.py --il dump.il --edges     # every edge, with its reason

`--il` takes the built Parsek.dll (disassembled with ikdasm, mono-devel) or a
ready `ikdasm` dump. Report-only: the exit code is 0 unless the inputs cannot
be read.

Disagreement reasons (an edge carries every reason its occurrences show):

Text sees the edge, the compiled code does not:
- const: `B.Name` where Name is a const (literal) field of B; the compiler
  inlines the value, so no reference to B is emitted.
- enum-constant: `B.Member` of an enum B, the same inlining. Enums reference
  nothing, so these edges never reach a knot.
- nameof: `nameof(B)` / `nameof(B.X)` compiles to a string.
- nested-qualifier: `B.Nested` names the outer type only as a qualifier; the
  compiled reference is to the nested type alone.
- attribute-argument: `typeof(B)` or `B.Const` inside an attribute, stored as a
  serialized string or value, not as a reference.
- text false positives, where the name is not the type at all:
  initializer-member (`new X { B = ... }` sets a member named B),
  qualified-member (`Q.B` where Q is a Parsek type and B is its member, an
  enum member or property named like a type), foreign-qualifier (`Q.B` where
  Q is not a Parsek type: a stock type such as `Contract.State`, a property
  chain, or interpolated-string holes stripped together),
  null-conditional-member (`x?.B`), enum-cast (`(B)value` of an enum, which
  compiles to nothing), declared-member-name (`Contract.State State;`
  declares a member named B), foreign-generic (`B<T>` where the Parsek B is
  not generic, so the name is another assembly's type, such as
  System.Comparison<T>).
- other: an occurrence none of the above explains (listed with its context).

The compiled code uses the edge, the text never counts it:
- unnamed:<origins>: A's source never names B; the compiler still emits it
  (a member reached through a value of type B, `var` locals, inferred generic
  arguments, extension methods). The origins say where the IL names it:
  member, local, generic, operand, signature, field, header, custom, ...
- text-rule:<rules>: A's source names B but the text scan's own rules drop
  every occurrence: base-list (B is A's base type or interface; the scan reads
  type bodies, not headers), plain-call (`B(` read as a method call),
  value-member (`x.B`, read as a member of a value), member-name (A declares a
  member named B), enum-member.

Forwarders (semantic graph against the raw IL graph): forwarder.

Pure functions take data and return data; `main` is the I/O shell.
"""

from __future__ import annotations

import argparse
import re
import sys
from collections import Counter, defaultdict
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import archview  # noqa: E402
import ilgraph  # noqa: E402

# The text ratchet's pinned cut order, replayed here so the report shows both.
TEXT_CUT_ORDER = archview.RATCHET_CUT_ORDER

# The cut order the semantic residue ratchet replays
# (test_semantic.test_the_semantic_residue_ratchet): the semantic greedy
# sequence on the refactor branch at 668a03255 (after wave 2 W1c and W3b,
# 2026-11-08). Re-pin it only together with a tightened bound, never to
# loosen one.
SEMANTIC_CUT_ORDER = [
    "GhostPlaybackLogic",
    "RecordingTree",
    "FlightRecorder",
    "ParsekFlight",
    "ParsekScenario",
    "GameStateRecorder",
    "RecordingStore",
    "EffectiveState",
    "GhostMapPresence",
    "VesselSpawner",
    "RouteOrchestrator",
    "RecalculationEngine",
]

TEXT_ONLY_EXPLAINED = frozenset(
    [
        "const", "enum-constant", "enum-cast", "nameof", "nested-qualifier",
        "attribute-argument", "initializer-member", "null-conditional-member",
        "qualified-member", "foreign-qualifier", "declared-member-name", "foreign-generic",
    ]
)
NAMEOF_RE = re.compile(r"nameof\s*\(\s*(?:[\w.]*\.)?\s*$")
MEMBER_AFTER_RE = re.compile(r"^\s*\.\s*([A-Za-z_]\w*)")


def text_graph(model):
    return {entry["name"]: entry["referencesTo"] for entry in model["types"]}


def residue_members(type_graph, order):
    """Members of the largest component left after `order`'s outgoing edges go."""
    removed = set(order)
    nodes = sorted(type_graph)
    edges = [
        (name, target)
        for name in nodes
        if name not in removed
        for target in type_graph[name]
        if target != name and target in type_graph
    ]
    parts = archview.sccs(nodes, edges)
    largest = max(parts, key=len, default=[])
    return sorted(largest) if len(largest) >= 2 else []


def measure(type_graph, type_modules, order):
    """Knot size, greedy cuts and residue, replayed residue for one type graph."""
    knot_list = archview.knots(type_graph, type_modules)
    if not knot_list:
        return {"knot": 0, "cuts": [], "greedy": 0, "replayed": 0, "residue": []}
    cuts = archview.greedy_sink_cuts(knot_list[0]["members"], type_graph)
    replayed = archview.replay_cut_residue(type_graph, order)
    return {
        "knot": knot_list[0]["size"],
        "cuts": cuts,
        "greedy": cuts[-1]["sizeAfter"] if cuts else knot_list[0]["size"],
        "replayed": replayed,
        "residue": residue_members(type_graph, order),
    }


def edge_set(type_graph):
    return {(name, target) for name, targets in type_graph.items() for target in targets}


def type_occurrences(stripped, declarations, type_name, wanted):
    """{ident: [(rule, prefix, suffix)]} for idents in `wanted` inside `type_name`'s bodies.

    Mirrors archview.type_references, but keeps every occurrence with the rule
    that decided it: counted, plain-call, value-member, member-name, enum-member.
    """
    found = defaultdict(list)
    by_parent = archview.declarations_by_parent(declarations)
    for index, declaration in enumerate(declarations):
        if declaration["name"] != type_name:
            continue
        segments = archview.own_body_segments(declarations, index, by_parent)
        is_enum = declaration["kind"] == "enum"
        members = set()
        if not is_enum:
            members = archview.declared_member_names("".join(stripped[a:b] for a, b in segments))
        for seg_start, seg_end in segments:
            raw = stripped[seg_start:seg_end]
            text = archview.blank_enum_member_names(raw) if is_enum else raw
            for match in archview.IDENT_RE.finditer(raw):
                ident = match.group(0)
                if ident not in wanted or ident == type_name:
                    continue
                start, end = match.start(), match.end()
                if is_enum and text[start:end] != ident:
                    rule = "enum-member"
                elif archview._is_plain_call(text, start, end):
                    rule = "plain-call"
                else:
                    qualifier = archview._member_access_qualifier(text, start)
                    if qualifier is not None and not re.fullmatch(r"[A-Z]\w*", qualifier):
                        rule = "value-member"
                    elif ident in members and qualifier != "Parsek":
                        rule = "member-name"
                    else:
                        rule = "counted"
                prefix = raw[max(0, start - 240) : start]
                suffix = raw[end : end + 80]
                found[ident].append((rule, prefix, suffix))
    return found


def il_facts(parsed):
    """Literal names and nested type names per short type name, from parse_il output."""
    literals = defaultdict(set)
    for owner, names in parsed.get("literals", {}).items():
        literals[ilgraph.short_name(owner)].update(names)
    nested = defaultdict(set)
    for path in parsed.get("types", []):
        parts = path.split("/")
        if len(parts) >= 2 and not ilgraph.is_generated(parts[-1]):
            nested[ilgraph.short_name("/".join(parts[:-1]))].add(parts[-1])
    generic = {ilgraph.short_name(path) for path in parsed.get("genericTypes", [])}
    return literals, nested, generic


def _in_attribute(prefix):
    """True when the occurrence sits inside an attribute's `[...]`."""
    cut = max(prefix.rfind(";"), prefix.rfind("{"), prefix.rfind("}"), prefix.rfind("]"))
    return prefix.rfind("[") > cut


def classify_text_occurrence(prefix, suffix, target, kinds, literals, nested, generic=()):
    """One reason for a counted text occurrence of `target` the compiled code lacks."""
    if _in_attribute(prefix):
        return "attribute-argument"
    if NAMEOF_RE.search(prefix):
        return "nameof"
    before = prefix.rstrip()
    if re.match(r"\s*=(?!=)", suffix) and before.endswith(("{", ",")):
        return "initializer-member"
    if before.endswith("?."):
        return "null-conditional-member"
    qualifier = archview._member_access_qualifier(prefix, len(prefix))
    if qualifier is not None and qualifier != "Parsek":
        if qualifier in kinds and target not in nested.get(qualifier, ()):
            return "qualified-member"
        if qualifier not in kinds:
            return "foreign-qualifier"
    member = MEMBER_AFTER_RE.match(suffix)
    if member:
        name = member.group(1)
        # An interpolated string's holes are stripped together, so the member
        # can run into the next hole's text (`MODIDParsekFlight`).
        if any(name == lit or name.startswith(lit) for lit in literals.get(target, ())):
            return "enum-constant" if kinds.get(target) == "enum" else "const"
        if name in nested.get(target, ()):
            return "nested-qualifier"
    if kinds.get(target) == "enum" and before.endswith("(") and suffix.lstrip().startswith(")"):
        return "enum-cast"
    if re.match(r"\s*(?:;|=(?!=)|\{|=>)", suffix) and re.search(r"[\w>\]?]\s+$", prefix):
        return "declared-member-name"
    if suffix.lstrip().startswith("<") and target not in generic:
        return "foreign-generic"
    return "other"


def classify_edges(model, parsed, source_root, text_edges, il_edges):
    """{(from, to): (side, reasons, evidence)} for every edge in exactly one graph.

    side is "text-only" or "il-only"; reasons a sorted tuple; evidence one
    short context string for an unexplained text-only edge.
    """
    types = {entry["name"]: entry for entry in model["types"]}
    kinds = {name: entry["kind"] for name, entry in types.items()}
    literals, nested, generic = il_facts(parsed)
    text_only = text_edges - il_edges
    il_only = il_edges - text_edges
    wanted_by_type = defaultdict(set)
    for frm, to in text_only | il_only:
        wanted_by_type[frm].add(to)

    occurrences = defaultdict(lambda: defaultdict(list))
    cache = {}
    source_root = Path(source_root)
    for type_name, wanted in wanted_by_type.items():
        entry = types.get(type_name)
        if entry is None:
            continue
        for rel_path in entry["files"]:
            if rel_path not in cache:
                text = (source_root / rel_path).read_text(encoding="utf-8-sig", errors="replace")
                stripped = archview.strip_comments_and_strings(text)
                cache[rel_path] = (stripped, archview.type_declarations(stripped))
            stripped, declarations = cache[rel_path]
            for ident, rows in type_occurrences(stripped, declarations, type_name, wanted).items():
                occurrences[type_name][ident].extend(rows)

    origins = defaultdict(set)
    for owner, owner_uses in parsed["uses"].items():
        source = ilgraph.short_name(owner)
        if source not in wanted_by_type:
            continue
        for use in owner_uses:
            target = ilgraph.short_name(use[1])
            if target in wanted_by_type[source]:
                origins[(source, target)].add("member" if use[0] == "member" else use[2])

    result = {}
    for frm, to in sorted(text_only):
        rows = [row for row in occurrences[frm][to] if row[0] == "counted"]
        reasons = set()
        evidence = ""
        for _rule, prefix, suffix in rows:
            reason = classify_text_occurrence(prefix, suffix, to, kinds, literals, nested, generic)
            reasons.add(reason)
            if reason == "other" and not evidence:
                evidence = (prefix[-50:] + "<<" + to + ">>" + suffix[:30]).replace("\n", " ")
        if not rows:
            reasons.add("other")
            evidence = "no counted occurrence in the declaring files"
        result[(frm, to)] = ("text-only", tuple(sorted(reasons)), " ".join(evidence.split()))
    for frm, to in sorted(il_only):
        rows = occurrences[frm][to]
        if to in types[frm]["bases"]:
            # The text scan reads type bodies; the base list is in the header.
            reason = "text-rule:base-list"
        elif not rows:
            reason = "unnamed:" + ",".join(sorted(origins[(frm, to)]) or ["?"])
        else:
            rules = sorted({row[0] for row in rows})
            reason = "text-rule:" + ",".join(rules)
        result[(frm, to)] = ("il-only", (reason,), "")
    return result


def forwarder_edges(raw_graph, semantic_graph):
    """Edges the forwarder rule removes and adds."""
    raw_edges, semantic_edges = edge_set(raw_graph), edge_set(semantic_graph)
    return sorted(raw_edges - semantic_edges), sorted(semantic_edges - raw_edges)


def reason_counts(classified):
    counts = Counter()
    for side, reasons, _evidence in classified.values():
        for reason in reasons:
            family = reason.split(":")[0] if side == "il-only" else reason
            counts[(side, family)] += 1
    return counts


def build(model, il_text, source_root):
    """Everything the report and the tests need, as one dict."""
    parsed = ilgraph.parse_il(il_text)
    names = {entry["name"] for entry in model["types"]}
    type_modules = {entry["name"]: entry["module"] for entry in model["types"]}
    text = text_graph(model)
    raw = ilgraph.type_graph_from_il(parsed, names, resolve_forwarders=False)
    semantic = ilgraph.type_graph_from_il(parsed, names, resolve_forwarders=True)
    return {
        "parsed": parsed,
        "text": text,
        "raw": raw,
        "semantic": semantic,
        "typeModules": type_modules,
        "textMeasure": measure(text, type_modules, TEXT_CUT_ORDER),
        "semanticMeasure": measure(semantic, type_modules, SEMANTIC_CUT_ORDER),
        "semanticTextOrder": archview.replay_cut_residue(semantic, TEXT_CUT_ORDER),
        "rawMeasure": measure(raw, type_modules, SEMANTIC_CUT_ORDER),
        "classified": classify_edges(
            model, parsed, source_root, edge_set(text), edge_set(raw)
        ),
        "forwarderCalls": ilgraph.forwarder_calls(parsed, names),
        "forwarderEdges": forwarder_edges(raw, semantic),
    }


def ratchet_residue(measured):
    """The ratchet's measure: min(greedy residue, replayed residue)."""
    return min(measured["greedy"], measured["replayed"])


def print_report(result, show_edges=False):
    text_m, sem_m, raw_m = result["textMeasure"], result["semanticMeasure"], result["rawMeasure"]
    print("SEMANTIC (compiled IL, forwarders read as calls) against the TEXT scan:")
    print(
        "  text:     knot=%d greedy residue=%d replayed residue=%d (text cut order)"
        % (text_m["knot"], text_m["greedy"], text_m["replayed"])
    )
    print(
        "  raw IL:   knot=%d greedy residue=%d replayed residue=%d (semantic cut order)"
        % (raw_m["knot"], raw_m["greedy"], raw_m["replayed"])
    )
    print(
        "  semantic: knot=%d greedy residue=%d replayed residue=%d (semantic cut order),"
        " %d with the text cut order"
        % (sem_m["knot"], sem_m["greedy"], sem_m["replayed"], result["semanticTextOrder"])
    )
    print("  ratchet measure (min of greedy and replayed): text=%d semantic=%d"
          % (ratchet_residue(text_m), ratchet_residue(sem_m)))
    print("  Semantic greedy cuts:")
    for cut in sem_m["cuts"]:
        print("    %d -> cut %s -> %d" % (cut["sizeBefore"], cut["sink"], cut["sizeAfter"]))
    text_edges, raw_edges = edge_set(result["text"]), edge_set(result["raw"])
    print(
        "  Type edges: text=%d raw IL=%d semantic=%d; text-only=%d, IL-only=%d"
        % (
            len(text_edges),
            len(raw_edges),
            len(edge_set(result["semantic"])),
            len(text_edges - raw_edges),
            len(raw_edges - text_edges),
        )
    )
    counts = reason_counts(result["classified"])
    print("  Disagreement reasons (an edge with mixed occurrences counts once per reason):")
    for (side, family), count in sorted(counts.items(), key=lambda item: (item[0][0], -item[1])):
        print("    %-9s %-20s %d" % (side, family, count))
    removed, added = result["forwarderEdges"]
    print(
        "  Forwarder rule: %d resolved call sites, %d edges removed, %d added"
        % (len(result["forwarderCalls"]), len(removed), len(added))
    )
    text_res, sem_res = set(text_m["residue"]), set(sem_m["residue"])
    by_module = Counter(result["typeModules"].get(name, "?") for name in text_res - sem_res)
    print(
        "  Residue membership: %d in both, %d text only, %d semantic only"
        % (len(text_res & sem_res), len(text_res - sem_res), len(sem_res - text_res))
    )
    print("    text only by module: %s" % ", ".join("%s=%d" % kv for kv in by_module.most_common()))
    by_module = Counter(result["typeModules"].get(name, "?") for name in sem_res - text_res)
    print("    semantic only by module: %s" % ", ".join("%s=%d" % kv for kv in by_module.most_common()))
    unexplained = [
        (edge, value)
        for edge, value in result["classified"].items()
        if value[0] == "text-only" and "other" in value[1]
    ]
    print("  Unexplained text-only edges (%d):" % len(unexplained))
    for (frm, to), (_side, _reasons, evidence) in unexplained:
        print("    %s -> %s: %s" % (frm, to, evidence))
    if show_edges:
        print("  Every disagreeing edge:")
        for (frm, to), (side, reasons, _evidence) in sorted(result["classified"].items()):
            print("    %-9s %s -> %s: %s" % (side, frm, to, ", ".join(reasons)))
        print("  Residue membership, with the disagreeing edges that touch each type:")
        both = text_res | sem_res
        reasons_by_edge = {
            edge: "%s %s" % (value[0], "/".join(value[1]))
            for edge, value in result["classified"].items()
        }
        for edge in removed:
            reasons_by_edge.setdefault(edge, "forwarder-removed")
        for edge in added:
            reasons_by_edge.setdefault(edge, "forwarder-added")
        for name in sorted(both - (text_res & sem_res)):
            side = "text residue only" if name in text_res else "semantic residue only"
            touching = [
                "%s -> %s (%s)" % (frm, to, why)
                for (frm, to), why in sorted(reasons_by_edge.items())
                if (frm == name and to in both) or (to == name and frm in both)
            ]
            if name in TEXT_CUT_ORDER and name not in SEMANTIC_CUT_ORDER:
                touching.insert(0, "cut in the text order only")
            elif name in SEMANTIC_CUT_ORDER and name not in TEXT_CUT_ORDER:
                touching.insert(0, "cut in the semantic order only")
            print("    %s [%s]: %s" % (name, side, "; ".join(touching) or "no disagreeing edge;"
                                       " it moved because of edges between other types"))
        print("  Forwarder rewrites (caller, forwarder type, member, resolved type):")
        for row in result["forwarderCalls"]:
            print("    %s -> %s.%s => %s" % row)
        for frm, to in removed:
            print("    removed %s -> %s" % (frm, to))
        for frm, to in added:
            print("    added   %s -> %s" % (frm, to))


def main(argv=None):
    parser = argparse.ArgumentParser(description="Semantic (compiled IL) type graph report.")
    parser.add_argument("--il", required=True, help="Parsek.dll (needs ikdasm) or an ikdasm .il dump")
    parser.add_argument("--source", default=archview.DEFAULT_SOURCE, help="module source root")
    parser.add_argument("--modules", default=str(archview.DEFAULT_MODULES), help="modules.toml")
    parser.add_argument("--edges", action="store_true", help="list every disagreeing edge")
    args = parser.parse_args(argv)
    rules, tooling, _forbidden, _allowed = archview.load_rules(args.modules)
    model = archview.build_model(args.source, rules, tooling, measure_sizes=False)
    result = build(model, ilgraph.load_il(args.il), args.source)
    print_report(result, args.edges)
    return 0


if __name__ == "__main__":
    sys.exit(main())
