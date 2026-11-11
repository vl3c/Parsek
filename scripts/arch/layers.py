#!/usr/bin/env python3
"""Declared layers: the intended dependency order of the production modules,
the edges that run against it, and the ratchet that keeps them from growing.

`modules.toml` declares the order under `[layers]` (lowest first) and puts
every production module in exactly one layer under `[layers.modules]`. An
edge from a module to a module in a HIGHER layer is backward; an edge inside
one layer is lateral (reported, never ratcheted); an edge to a lower layer is
the intended direction. Unlike the Atlas's instability-based "upward" edges,
the direction of an edge here does not move when instabilities drift: it only
changes when someone edits the declaration.

An edge is measured as the number of DISTINCT (from type, to type) pairs
between the two modules, so it moves neither with the number of call sites
nor with comments. CI measures it on the compiled-IL semantic graph (the
forwarder-resolved graph `semantic.build` returns as "semantic"); the text
scan (`archview.py --check`, the Atlas) shows the same view over the text type
graph, for reading without a DLL.

The ratchet (`test_semantic_layers.RealTreeLayerTests`, CI's compiled-IL step)
reads `layer_pins.toml`, machine-written from a Debug build:

    python3 scripts/arch/layers.py Source/Parsek/bin/Debug/Parsek.dll              # report
    python3 scripts/arch/layers.py Source/Parsek/bin/Debug/Parsek.dll --write-pins # re-pin

A backward module pair may not count more type pairs than its pin, and a
backward module pair with no pin reds. The gate reads the counts only; each
pin also lists its type pairs, used for nothing but naming what is new in a
failure message (so a rename moves no count and reds nothing). A count may
fall without a re-pin; the slack is not enforced until someone re-pins, the
same contract as facts_pins.toml.

The `[forbidden]` edges of modules.toml are gated too: any type pair from the
first module to the second reds, on the semantic graph here and on the text
scan's module edges in the DLL-free suite (`test_archview`).

Everything except `main` and the loaders is pure. This module imports
nothing from the tools at load time, so `archview` can import it.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
import tomllib
from collections import defaultdict
from pathlib import Path

HERE = Path(__file__).resolve().parent
DEFAULT_MODULES = HERE / "modules.toml"
DEFAULT_PINS = HERE / "layer_pins.toml"
REPIN_COMMAND = (
    "python3 scripts/arch/layers.py Source/Parsek/bin/Debug/Parsek.dll --write-pins"
    " (after a Debug build of this tree; never from a Release build)"
)
# How many type pairs a failure message or a report line names before "+N more".
NAMED_PAIRS = 12


# --- the declaration -----------------------------------------------------------------


def load_layers(path=DEFAULT_MODULES):
    """The raw `[layers]` table of a modules.toml ({} when it has none)."""
    with open(path, "rb") as handle:
        return tomllib.load(handle).get("layers", {})


def declared_modules(path=DEFAULT_MODULES):
    """(production module names, tooling module names) the rules of a modules.toml declare."""
    with open(path, "rb") as handle:
        data = tomllib.load(handle)
    names = {rule.get("name") for rule in data.get("module", []) if rule.get("name")}
    tooling = set(data.get("tooling", {}).get("modules", []))
    return names - tooling, tooling


def layer_problems(layers, production, tooling=()):
    """Why `layers` is not a complete, well-formed declaration (empty when it is).

    Every production module sits in exactly one layer named in `order`; a
    tooling module or a name no rule declares has no layer.
    """
    problems = []
    order = layers.get("order")
    if not isinstance(order, list) or not order or not all(isinstance(name, str) for name in order):
        return ["[layers] order must be a non-empty list of layer names, lowest first"]
    if len(set(order)) != len(order):
        problems.append("[layers] order names a layer twice: %s" % ", ".join(order))
    table = layers.get("modules")
    if not isinstance(table, dict):
        return problems + ["[layers.modules] is missing: one list of modules per layer"]
    seen = {}
    for layer, modules in table.items():
        if layer not in order:
            problems.append("[layers.modules] %s is not a layer in [layers] order" % layer)
        if not isinstance(modules, list) or not all(isinstance(m, str) for m in modules):
            problems.append("[layers.modules] %s must be a list of module names" % layer)
            continue
        for module in modules:
            if module in seen:
                problems.append("%s is in two layers: %s and %s" % (module, seen[module], layer))
                continue
            seen[module] = layer
            if module in tooling:
                problems.append("%s is a tooling module; tooling modules have no layer" % module)
            elif module not in production:
                problems.append("%s has a layer but no rule in modules.toml declares it" % module)
    for layer in order:
        if not table.get(layer):
            problems.append("layer %s has no module" % layer)
    for module in sorted(set(production) - set(seen)):
        problems.append(
            "%s has no layer: add it to one list under [layers.modules] in modules.toml" % module
        )
    return problems


def layer_of(layers):
    """{module: (layer index, layer name)} for every module the declaration places."""
    order = layers.get("order") or []
    out = {}
    for layer, modules in (layers.get("modules") or {}).items():
        if layer in order and isinstance(modules, list):
            for module in modules:
                out.setdefault(module, (order.index(layer), layer))
    return out


# --- the measure ---------------------------------------------------------------------


def edge_key(frm, to):
    return "%s -> %s" % (frm, to)


def module_type_pairs(type_graph, type_modules):
    """{(from module, to module): {(from type, to type)}} over the cross-module edges.

    `type_graph` is {type: [types it references]}; a type without a module is
    left out. Each distinct type pair counts once, however many call sites,
    fields or locals carry it.
    """
    pairs = defaultdict(set)
    for name, targets in type_graph.items():
        frm = type_modules.get(name)
        if frm is None:
            continue
        for target in targets:
            to = type_modules.get(target)
            if to is None or to == frm:
                continue
            pairs[(frm, to)].add((name, target))
    return dict(pairs)


def classify(pairs, layers):
    """Split module pairs by direction: {"backward", "lateral", "downward"}, each
    {(from, to): {type pairs}}. A pair touching a module without a layer is
    left out (layer_problems names that module)."""
    placed = layer_of(layers)
    out = {"backward": {}, "lateral": {}, "downward": {}}
    for key, type_pairs in pairs.items():
        frm, to = key
        if frm not in placed or to not in placed:
            continue
        if placed[frm][0] < placed[to][0]:
            out["backward"][key] = set(type_pairs)
        elif placed[frm][0] == placed[to][0]:
            out["lateral"][key] = set(type_pairs)
        else:
            out["downward"][key] = set(type_pairs)
    return out


def summary(classified):
    """The headline numbers the report, the Atlas and the metrics row print."""
    backward, lateral = classified["backward"], classified["lateral"]
    return {
        "backwardModulePairs": len(backward),
        "backwardTypePairs": sum(len(v) for v in backward.values()),
        "lateralModulePairs": len(lateral),
        "lateralTypePairs": sum(len(v) for v in lateral.values()),
    }


def text_pairs(model):
    """module_type_pairs over an archview model's text type graph."""
    types = model.get("types") or []
    return module_type_pairs(
        {entry["name"]: entry["referencesTo"] for entry in types},
        {entry["name"]: entry["module"] for entry in types},
    )


def text_module_edges(model):
    """{(from module, to module): [referenced type names]} from an archview model's
    module edges (the text scan's file-level references, tooling included): the
    graph the DLL-free forbidden-edge gate reads."""
    return {(edge["from"], edge["to"]): edge["types"] for edge in model.get("edges", [])}


def semantic_graph(model, parsed):
    """The forwarder-resolved compiled-IL type graph over the model's nodes: the
    graph `semantic.build` returns as "semantic", without the text comparison."""
    import ilgraph  # noqa: PLC0415

    names = {entry["name"] for entry in model["types"]}
    return ilgraph.type_graph_from_il(parsed, names, resolve_forwarders=True)


def _named(type_pairs, limit=NAMED_PAIRS):
    rows = sorted(type_pairs)
    text = ", ".join(row if isinstance(row, str) else "%s -> %s" % row for row in rows[:limit])
    if len(rows) > limit:
        text += " +%d more" % (len(rows) - limit)
    return text or "none"


# --- forbidden edges -------------------------------------------------------------------


def forbidden_problems(forbidden, pairs, modules):
    """Why the declared [forbidden] edges fail on `pairs` (empty when none is crossed).

    `pairs` is {(from, to): {type pairs}} (or any container per key); `modules`
    the module names that exist. A malformed spec or one naming a module that
    does not exist reds too, so a typo or a renamed module cannot switch the
    gate off.
    """
    problems = []
    for spec in forbidden:
        parts = [part.strip() for part in spec.split("->")] if isinstance(spec, str) else []
        if len(parts) != 2 or not parts[0] or not parts[1]:
            problems.append("[forbidden] spec %r is not \"From -> To\"" % (spec,))
            continue
        frm, to = parts
        unknown = [name for name in (frm, to) if name not in modules]
        if unknown:
            problems.append("[forbidden] %s names no module: %s" % (spec, ", ".join(unknown)))
            continue
        crossed = pairs.get((frm, to))
        if crossed:
            problems.append(
                "forbidden edge %s is crossed (%d): %s. Remove the dependency;"
                " the boundary is declared in scripts/arch/modules.toml [forbidden]"
                % (edge_key(frm, to), len(crossed), _named(crossed))
            )
    return problems


# --- the ratchet -----------------------------------------------------------------------


def load_pins(path=DEFAULT_PINS):
    with open(path, "rb") as handle:
        return tomllib.load(handle)


def pinned(pins):
    """{"A -> B": {"count": n, "pairs": [...]}} from a loaded pins file."""
    return {entry["edge"]: entry for entry in pins.get("backward", [])}


def render_pins(backward, layers, source="", date=""):
    placed = layer_of(layers)
    total = sum(len(v) for v in backward.values())
    lines = [
        "# The declared-layer ratchet (scripts/arch/layers.py), read by",
        "# scripts/arch/test_semantic_layers.py in CI's compiled-IL step. Machine-written:",
        "#   " + REPIN_COMMAND,
        "# One entry per backward module pair (a module depending on one in a higher layer",
        "# of modules.toml [layers]), measured on the compiled-IL semantic graph as the number",
        "# of distinct (from type, to type) pairs. The gate reads `count` only: a pair may not",
        "# rise above it, and a backward module pair with no entry reds. `pairs` lists the type",
        "# pairs at the pin, used only to name what is new in a failure message, so a rename",
        "# reds nothing. A count may fall without a re-pin; the slack is not enforced until",
        "# someone re-pins, so re-pin in the PR that lowers a count, and raise one only for a",
        "# deliberate change the PR explains.",
        "# %d backward module pairs, %d type pairs." % (len(backward), total),
    ]
    if source or date:
        lines.append("# Pinned from %s %s" % (source, date))
    for key in sorted(backward, key=lambda k: (-len(backward[k]), k)):
        frm, to = key
        lines.append("")
        lines.append("[[backward]]")
        lines.append("edge = %s" % json.dumps(edge_key(frm, to)))
        lines.append("layers = %s" % json.dumps(
            "%s -> %s" % (placed.get(frm, (0, "?"))[1], placed.get(to, (0, "?"))[1])))
        lines.append("count = %d" % len(backward[key]))
        lines.append("pairs = [")
        lines.extend("    %s," % json.dumps("%s -> %s" % row) for row in sorted(backward[key]))
        lines.append("]")
    return "\n".join(lines) + "\n"


def ratchet_problems(backward, pins, layers=None):
    """Why the measured backward pairs break the pins (empty when they hold)."""
    placed = layer_of(layers or {})
    table = pinned(pins)
    problems = []
    for key in sorted(backward):
        frm, to = key
        spec = edge_key(frm, to)
        measured = backward[key]
        where = ""
        if frm in placed and to in placed:
            where = " (%s below %s)" % (placed[frm][1], placed[to][1])
        entry = table.get(spec)
        if entry is None:
            problems.append(
                "new backward module pair %s%s: %d type pairs: %s. Depend the other way (an"
                " interface or a callback in the lower module) or move the type; if the"
                " dependency is deliberate, re-pin: %s"
                % (spec, where, len(measured), _named(measured), REPIN_COMMAND)
            )
            continue
        if len(measured) > entry["count"]:
            at_pin = {tuple(part.strip() for part in row.split("->")) for row in entry.get("pairs", [])}
            new = measured - at_pin
            gone = at_pin - measured
            problems.append(
                "backward module pair %s%s: %d type pairs, pinned %d (+%d). New since the pin: %s.%s"
                " If the dependency is deliberate, re-pin: %s"
                % (spec, where, len(measured), entry["count"], len(measured) - entry["count"],
                   _named(new), (" Gone since the pin: %s." % _named(gone)) if gone else "",
                   REPIN_COMMAND)
            )
    return problems


def ratchet_slack(backward, pins):
    """Pins the measure is now below (a re-pin would tighten them)."""
    out = []
    for spec, entry in sorted(pinned(pins).items()):
        frm, to = [part.strip() for part in spec.split("->")]
        count = len(backward.get((frm, to), ()))
        if count < entry["count"]:
            out.append("%s: %d below the pin %d" % (spec, count, entry["count"]))
    return out


# --- report ------------------------------------------------------------------------------


def format_report(classified, layers, title, weights=None):
    """Plain-text lines: the layers, every backward pair, the lateral pairs."""
    placed = layer_of(layers)
    head = summary(classified)
    lines = [title]
    for index, layer in enumerate(layers.get("order") or []):
        members = sorted(m for m, (_i, name) in placed.items() if name == layer)
        lines.append("  layer %d %-13s %s" % (index, layer, ", ".join(members)))
    lines.append(
        "  %d backward module pairs (%d type pairs); %d lateral module pairs (%d type pairs)"
        % (head["backwardModulePairs"], head["backwardTypePairs"],
           head["lateralModulePairs"], head["lateralTypePairs"])
    )
    backward = classified["backward"]
    lines.append("  Backward (a module depending on a higher layer), by type pairs:")
    if not backward:
        lines.append("    none.")
    for key in sorted(backward, key=lambda k: (-len(backward[k]), k)):
        frm, to = key
        weight = (" weight=%d" % weights[key]) if weights and key in weights else ""
        lines.append(
            "    %s (%s -> %s): type pairs=%d%s; %s"
            % (edge_key(frm, to), placed[frm][1], placed[to][1], len(backward[key]), weight,
               _named(backward[key], 4))
        )
    lateral = classified["lateral"]
    lines.append("  Lateral (same layer; reported, not ratcheted):")
    if not lateral:
        lines.append("    none.")
    for key in sorted(lateral, key=lambda k: (-len(lateral[k]), k)):
        lines.append("    %s (%s): type pairs=%d" % (edge_key(*key), placed[key[0]][1], len(lateral[key])))
    return lines


def main(argv=None):
    parser = argparse.ArgumentParser(description="Declared layers over the compiled-IL semantic graph.")
    parser.add_argument("input", help="Parsek.dll (disassembled with ikdasm) or an ikdasm .il dump")
    parser.add_argument("--write-pins", action="store_true", help="re-pin layer_pins.toml from this input")
    parser.add_argument("--pins", default=str(DEFAULT_PINS))
    parser.add_argument("--modules", default=str(DEFAULT_MODULES))
    parser.add_argument("--source", default=None, help="module source root (default Source/Parsek)")
    args = parser.parse_args(argv)
    if args.write_pins and re.search(r"[\\/]Release[\\/]", str(args.input)):
        parser.error("pin from the Debug build CI measures, not a Release build")
    sys.path.insert(0, str(HERE))
    import archview  # noqa: PLC0415
    import ilgraph  # noqa: PLC0415

    source = args.source or str(archview.REPO_ROOT / archview.DEFAULT_SOURCE)
    rules, tooling, forbidden, _allowed = archview.load_rules(args.modules)
    layers = load_layers(args.modules)
    production, tooling_names = declared_modules(args.modules)
    model = archview.build_model(source, rules, tooling, measure_sizes=False)
    graph = semantic_graph(model, ilgraph.load_parsed(args.input))
    pairs = module_type_pairs(graph, {entry["name"]: entry["module"] for entry in model["types"]})
    classified = classify(pairs, layers)
    print("\n".join(format_report(classified, layers, "DECLARED LAYERS (compiled-IL semantic graph):")))
    problems = layer_problems(layers, production, tooling_names)
    problems += forbidden_problems(forbidden, pairs, production | tooling_names)
    pins_path = Path(args.pins)
    if args.write_pins:
        import datetime  # noqa: PLC0415

        pins_path.write_text(
            render_pins(classified["backward"], layers, Path(args.input).name,
                        datetime.date.today().isoformat()),
            encoding="utf-8", newline="\n")
        print("Wrote %s" % pins_path)
    elif pins_path.is_file():
        pins = load_pins(pins_path)
        problems += ratchet_problems(classified["backward"], pins, layers)
        for line in ratchet_slack(classified["backward"], pins):
            print("  slack (re-pin to tighten): " + line)
    for line in problems or ["layers, forbidden edges and the ratchet hold"]:
        print("  " + line)
    return 0


if __name__ == "__main__":
    sys.exit(main())
