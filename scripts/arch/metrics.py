#!/usr/bin/env python3
"""One metrics history row for one commit: every headline number the
architecture tooling already measures, as a flat-ish JSON object.

On a push to main, CI builds a row for the commit and appends it to
`metrics.jsonl` on the `metrics` branch (scripts/arch/metrics_append.py), so
a later reader can draw trends. A pull request builds no row; its
compiled-IL step only checks that one can be built (test_metrics.py, over the
parse and facts that step already holds). The row gates nothing.

    python3 scripts/arch/metrics.py --sha <sha> --date <iso> \\
        [--dll Source/Parsek/bin/Debug/Parsek.dll] \\
        [--tests-dll Source/Parsek.Tests/bin/Debug/net472/Parsek.Tests.dll] [--out row.json]

Nothing here measures anything new. The numbers come from the tools that
already report them, called in one process so the assembly is disassembled
and parsed once (`ilgraph.load_parsed`) for both the semantic graph and the
code facts:

- text: archview.build_model over Source/Parsek (no DLL needed);
- semantic: semantic.build over that model and the Parsek.dll parse;
- facts: ilanalysis.report over ilfacts.build of the same parse, with test
  reach when the test DLL is given, the headless modules' leaks (headless.py)
  over the same game reach, and lifetimes.py's summary over the same facts;
- sizes: scripts/count-code.py (its `collect`).

An input that is not given (no --dll, no --tests-dll) leaves its half null;
an input that is given but cannot be measured raises, because a tool that
breaks must fail the CI step rather than write a quietly empty row.

Row schema `parsek-metrics/1` (keys are stable; a new key is additive, a
renamed or removed key bumps the schema id). Shares are fractions rounded to
4 places. Module maps are keyed by the module names in modules.toml.

    schema                    "parsek-metrics/1"
    commit.sha, commit.date   the commit measured; date is ISO 8601 UTC (Z)
    measured.text / .sizes    always true in a row the CLI writes
    measured.il               Parsek.dll was measured (semantic and facts set)
    measured.testsDll         Parsek.Tests.dll was measured (facts.testReach set)
    text.modules              production modules (tooling ones excluded)
    text.toolingModules       tooling modules
    text.productionFiles      production .cs files placed by modules.toml
    text.productionLines      their raw line count
    text.byModule.<M>         {files, lines} per production module
    text.unclassifiedFiles    files no modules.toml rule places (the CI gate wants 0)
    text.forbiddenEdges       {declared, violations}: modules.toml [forbidden] edges
    text.upwardEdges          production module edges against the stability gradient
    text.layers               the declared layers (modules.toml [layers]) over the text
                              type graph: {backwardModulePairs, backwardTypePairs,
                              lateralModulePairs, lateralTypePairs}; null without a declaration
    text.productionTypes      types in the text type graph
    text.knots                sizes of every knot (largest first), the canary list
    text.knot                 the largest knot
    text.greedyResidue        the largest knot after archview's greedy sink cuts
    text.replayedResidue      residue after archview.RATCHET_CUT_ORDER
    text.firstCut             the first greedy cut {sink, sizeBefore, sizeAfter}
    semantic.knot / .greedyResidue / .replayedResidue
                              the semantic (forwarder-resolved IL) graph
    semantic.textOrderResidue the semantic graph replayed with the text cut order
    semantic.ratchetResidue   min(greedy, replayed), what the semantic ratchet bounds
    semantic.text.*           the text graph measured by semantic.py (knot,
                              greedyResidue, replayedResidue, ratchetResidue)
    semantic.raw.*            the raw IL graph (knot, greedyResidue, replayedResidue)
    semantic.edges            {text, raw, semantic, textOnly, ilOnly} type edge counts
    semantic.forwarders       {callSites, edgesRemoved, edgesAdded}
    semantic.residueMembership {both, textOnly, semanticOnly}
    semantic.layers           the same four declared-layer numbers over the semantic graph
                              (what layer_pins.toml ratchets); null without a declaration
    facts.ratchets            {crossTypeStaticWrites, complexity50, complexityWatch,
                              uncalledInAssembly}: the measured sets' sizes
    facts.statics             {reassignable, readonlyCollections, mutatedCollections,
                              writtenElsewhere, mutatedElsewhere, addressTakenElsewhere}
    facts.complexity          {"20", "50", "100"}: methods at or above
    facts.coupling.share      share of production instructions that can reach the game
    facts.coupling.byModule.<M> {share, directShare, instructions, methods}
    facts.uncalled            {methods, instructions, noTestCaller, testOnly}
                              (the last two null without the test DLL)
    facts.testReach           null without the test DLL, else {byModule.<M>:
                              {methodShare, instructionShare, methods}, untestedComplex}
    facts.lifetimes           the declared static lifetimes (scripts/arch/lifetimes.py
                              summary): {population, tagged, untagged, byLifetime.<L>,
                              coldClosureMethods, findings.<F>}; a finding is null when
                              not measured (testWrittenUntagged needs the test DLL), and
                              the whole key is null when the report carries none
    facts.headless            the declared headless modules (scripts/arch/headless.py):
                              {modules, leaks, leakingMethods, seams, byModule.<M>:
                              {headless, methods, reachMethods, directMethods, leaks}};
                              leaks is null for a module not declared headless, and the
                              whole key is null when the report carries none
    sizes.areas.<area>        {files, lines, code} per count-code.py area name
    sizes.filesOutsideAreas   tracked code-like files outside every area
    sizes.tests               count-code.py's test totals, its key names

Pure functions build each half from the tool outputs; `main` is the I/O shell.
"""

from __future__ import annotations

import argparse
import datetime
import importlib.util
import json
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import archview  # noqa: E402

SCHEMA = "parsek-metrics/1"
COUNT_CODE = archview.REPO_ROOT / "scripts" / "count-code.py"
SHA_RE = re.compile(r"^[0-9a-f]{7,40}$")


def _share(value):
    return round(float(value), 4)


def normalize_date(text):
    """An ISO 8601 date-time as UTC `YYYY-MM-DDTHH:MM:SSZ` (a naive one is read as UTC)."""
    value = text.strip()
    if value.endswith("Z"):
        value = value[:-1] + "+00:00"
    moment = datetime.datetime.fromisoformat(value)
    if moment.tzinfo is None:
        moment = moment.replace(tzinfo=datetime.timezone.utc)
    return moment.astimezone(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def layer_numbers(type_graph, type_modules, layer_table):
    """The declared-layer headline (layers.summary) of one type graph; None
    without a `[layers]` declaration."""
    if not layer_table:
        return None
    import layers  # noqa: PLC0415

    return layers.summary(layers.classify(layers.module_type_pairs(type_graph, type_modules), layer_table))


def _graph_and_modules(types):
    return ({entry["name"]: entry["referencesTo"] for entry in types},
            {entry["name"]: entry.get("module") for entry in types})


def text_section(model, forbidden=(), layer_table=None):
    """The text scan's numbers from an archview.build_model result."""
    production = sorted(m["name"] for m in model["modules"] if not m["tooling"])
    by_module = {name: {"files": 0, "lines": 0} for name in production}
    for row in (model.get("sizes") or {}).get("files", []):
        if row["module"] in by_module:
            by_module[row["module"]]["files"] += 1
            by_module[row["module"]]["lines"] += row["lines"]
    edges = {(edge["from"], edge["to"]) for edge in model["edges"]}
    declared = violations = 0
    for spec in forbidden:
        try:
            pair = archview.parse_edge_spec(spec)
        except ValueError:
            continue
        declared += 1
        violations += pair in edges
    knot_list = model.get("knots") or []
    largest = knot_list[0] if knot_list else None
    cuts = (largest or {}).get("cuts") or []
    return {
        "modules": len(production),
        "toolingModules": sum(1 for m in model["modules"] if m["tooling"]),
        "productionFiles": sum(row["files"] for row in by_module.values()),
        "productionLines": sum(row["lines"] for row in by_module.values()),
        "byModule": by_module,
        "unclassifiedFiles": len(model.get("unclassified") or []),
        "forbiddenEdges": {"declared": declared, "violations": violations},
        "upwardEdges": archview._measure_upward_edges(model),
        "layers": layer_numbers(*_graph_and_modules(model.get("types") or []), layer_table),
        "productionTypes": len(model.get("types") or []),
        "knots": [knot["size"] for knot in knot_list],
        "knot": largest["size"] if largest else 0,
        "greedyResidue": cuts[-1]["sizeAfter"] if cuts else (largest["size"] if largest else 0),
        "replayedResidue": archview._measure_replayed_residue(model) or 0,
        "firstCut": (
            {key: cuts[0][key] for key in ("sink", "sizeBefore", "sizeAfter")} if cuts else None
        ),
    }


def semantic_section(result, layer_table=None):
    """The semantic report's headline from a semantic.build result."""
    import semantic  # noqa: PLC0415 (it imports archview and ilgraph)

    text_m, sem_m, raw_m = result["textMeasure"], result["semanticMeasure"], result["rawMeasure"]
    text_edges = semantic.edge_set(result["text"])
    raw_edges = semantic.edge_set(result["raw"])
    removed, added = result["forwarderEdges"]
    text_res, sem_res = set(text_m["residue"]), set(sem_m["residue"])
    return {
        "knot": sem_m["knot"],
        "greedyResidue": sem_m["greedy"],
        "replayedResidue": sem_m["replayed"],
        "textOrderResidue": result["semanticTextOrder"],
        "ratchetResidue": semantic.ratchet_residue(sem_m),
        "text": {
            "knot": text_m["knot"],
            "greedyResidue": text_m["greedy"],
            "replayedResidue": text_m["replayed"],
            "ratchetResidue": semantic.ratchet_residue(text_m),
        },
        "raw": {
            "knot": raw_m["knot"],
            "greedyResidue": raw_m["greedy"],
            "replayedResidue": raw_m["replayed"],
        },
        "edges": {
            "text": len(text_edges),
            "raw": len(raw_edges),
            "semantic": len(semantic.edge_set(result["semantic"])),
            "textOnly": len(text_edges - raw_edges),
            "ilOnly": len(raw_edges - text_edges),
        },
        "forwarders": {
            "callSites": len(result["forwarderCalls"]),
            "edgesRemoved": len(removed),
            "edgesAdded": len(added),
        },
        "residueMembership": {
            "both": len(text_res & sem_res),
            "textOnly": len(text_res - sem_res),
            "semanticOnly": len(sem_res - text_res),
        },
        "layers": layer_numbers(result["semantic"], result.get("typeModules") or {}, layer_table),
    }


STATIC_KEYS = (
    "reassignable",
    "readonlyCollections",
    "mutatedCollections",
    "writtenElsewhere",
    "mutatedElsewhere",
    "addressTakenElsewhere",
)


def facts_section(report):
    """The code facts' numbers from an ilanalysis.report result."""
    totals = report["statics"]["totals"]
    gates = report["gates"]
    dead = report["uncalled"]
    reach = report.get("testReach")
    coupling = {
        module: {
            "share": _share(row["share"]),
            "directShare": _share(row["directShare"]),
            "instructions": row["instructions"],
            "methods": row["methods"],
        }
        for module, row in report["coupling"]["byModule"].items()
    }
    test_reach = None
    if reach is not None:
        test_reach = {
            "byModule": {
                module: {
                    "methodShare": _share(row["methodShare"]),
                    "instructionShare": _share(row["instructionShare"]),
                    "methods": row["methods"],
                }
                for module, row in reach["byModule"].items()
            },
            "untestedComplex": len(reach["untestedComplex"]),
        }
    return {
        "ratchets": {name: len(gates[name]) for name in (
            "crossTypeStaticWrites", "complexity50", "complexityWatch", "uncalledInAssembly")},
        "statics": {key: totals.get(key, 0) for key in STATIC_KEYS},
        "complexity": {str(threshold): count for threshold, count in report["complexity"]["atLeast"].items()},
        "coupling": {"share": _share(report["coupling"]["share"]), "byModule": coupling},
        "uncalled": {
            "methods": len(dead["uncalled"]),
            "instructions": sum(dead["instructions"].values()),
            "noTestCaller": len(dead["noCaller"]) if "noCaller" in dead else None,
            "testOnly": len(dead["testOnly"]) if "testOnly" in dead else None,
        },
        "testReach": test_reach,
        "lifetimes": report.get("lifetimes"),
        "headless": headless_section(report.get("headless")),
    }


def headless_section(result):
    """The headless modules' numbers from an ilanalysis report's `headless` result (None stays None)."""
    if result is None:
        return None
    import headless  # noqa: PLC0415

    head = headless.summary(result)
    return {
        "modules": head["modules"],
        "leaks": head["leaks"],
        "leakingMethods": head["leakingMethods"],
        "seams": head["seams"],
        "byModule": {
            module: {key: row[key] for key in ("headless", "methods", "reachMethods", "directMethods", "leaks")}
            for module, row in head["byModule"].items()
        },
    }


def sizes_section(counts):
    """count-code.py's numbers from its `collect` payload (the head line is left out)."""
    return {
        "areas": {
            row["area"]: {"files": row["files"], "lines": row["lines"], "code": row["code"]}
            for row in counts["areas"]
        },
        "filesOutsideAreas": counts["filesOutsideAreas"],
        "tests": dict(counts["tests"]),
    }


def build_row(sha, date, text=None, semantic=None, facts=None, sizes=None, tests_dll=False):
    """The row from its already-built halves; a None half stays null.

    `tests_dll` says whether `facts` was measured with the test DLL.
    """
    sha = sha.strip().lower()
    if not SHA_RE.match(sha):
        raise ValueError("not a commit sha: %r" % sha)
    return {
        "schema": SCHEMA,
        "commit": {"sha": sha, "date": normalize_date(date)},
        "measured": {
            "text": text is not None,
            "sizes": sizes is not None,
            "il": semantic is not None and facts is not None,
            "testsDll": bool(tests_dll and facts is not None),
        },
        "text": text,
        "semantic": semantic,
        "facts": facts,
        "sizes": sizes,
    }


def dumps(row):
    """The row as one deterministic JSON line (sorted keys, no spaces), newline-terminated."""
    return json.dumps(row, sort_keys=True, separators=(",", ":"), ensure_ascii=True) + "\n"


# ---------------------------------------------------------------------------
# I/O shell
# ---------------------------------------------------------------------------


def load_count_code(path=COUNT_CODE):
    """scripts/count-code.py as a module (its file name is not importable).

    No bytecode is written: scripts/ has no __pycache__ ignore rule, and a
    stray .pyc there would show up as an untracked file.
    """
    spec = importlib.util.spec_from_file_location("count_code", path)
    module = importlib.util.module_from_spec(spec)
    saved, sys.dont_write_bytecode = sys.dont_write_bytecode, True
    try:
        spec.loader.exec_module(module)
    finally:
        sys.dont_write_bytecode = saved
    return module


def row_from_tools(sha, date, model, forbidden, semantic_result=None, report=None, counts=None,
                   tests_dll=False, layer_table=None):
    """The row from the tools' own results: an archview model and its [forbidden]
    specs, a semantic.build result, an ilanalysis.report result and a
    count-code collect() payload (None leaves that half null). `layer_table` is
    the `[layers]` table of modules.toml (None leaves both `layers` keys null).

    The CLI builds the inputs itself (`measure`); CI's compiled-IL step hands in
    the ones its other real-tree cells already built in the same process.
    """
    return build_row(
        sha,
        date,
        text_section(model, forbidden, layer_table),
        semantic_section(semantic_result, layer_table) if semantic_result is not None else None,
        facts_section(report) if report is not None else None,
        sizes_section(counts) if counts is not None else None,
        tests_dll=tests_dll,
    )


def measure(sha, date, dll=None, tests_dll=None, source=archview.DEFAULT_SOURCE,
            modules=archview.DEFAULT_MODULES):
    """Run every tool and return the row. Raises when a given input cannot be measured."""
    build_row(sha, date)  # a bad sha or date fails before minutes of measuring
    if tests_dll and not dll:
        raise ValueError("--tests-dll needs --dll (test reach is measured against Parsek.dll)")
    source = str(archview.REPO_ROOT / source) if not Path(source).is_absolute() else source
    rules, tooling, forbidden, _allowed = archview.load_rules(modules)
    model = archview.build_model(source, rules, tooling)
    semantic_result = report = None
    if dll:
        import ilanalysis  # noqa: PLC0415
        import ilfacts  # noqa: PLC0415
        import ilgraph  # noqa: PLC0415
        import semantic  # noqa: PLC0415

        dll = Path(dll)
        if not dll.is_file():
            raise FileNotFoundError("--dll %s does not exist (build Parsek first)" % dll)
        parsed = ilgraph.load_parsed(dll)
        semantic_result = semantic.build(model, None, source, parsed=parsed)
        facts = ilfacts.build(parsed, ilfacts.source_type_modules(source, rules))
        tests_facts = None
        if tests_dll:
            tests_dll = Path(tests_dll)
            if not tests_dll.is_file():
                raise FileNotFoundError("--tests-dll %s does not exist" % tests_dll)
            tests_facts = ilanalysis.load_facts(tests_dll, None)[0]
        import headless  # noqa: PLC0415

        report = ilanalysis.report(facts, tooling, ilanalysis.load_config(), tests_facts,
                                   headless.declared(headless.load_headless(modules)))
        import lifetimes  # noqa: PLC0415

        report["lifetimes"] = lifetimes.summary(lifetimes.analyse_facts(facts, tooling, tests_dll))
    import layers  # noqa: PLC0415

    return row_from_tools(sha, date, model, forbidden, semantic_result, report,
                          load_count_code().collect(), tests_dll=bool(tests_dll),
                          layer_table=layers.load_layers(modules))


def main(argv=None):
    parser = argparse.ArgumentParser(description="Build one metrics history row (schema %s)." % SCHEMA)
    parser.add_argument("--sha", required=True, help="the commit the row describes")
    parser.add_argument("--date", required=True, help="its date, ISO 8601 (git show -s --format=%%cI)")
    parser.add_argument("--dll", help="the Debug Parsek.dll (semantic graph and code facts)")
    parser.add_argument("--tests-dll", help="Parsek.Tests.dll (test reach); needs --dll")
    parser.add_argument("--out", help="write the row here (default: stdout)")
    args = parser.parse_args(argv)
    row = measure(args.sha, args.date, args.dll, args.tests_dll)
    text = dumps(row)
    if args.out:
        Path(args.out).write_text(text, encoding="utf-8", newline="\n")
        print("Wrote %s (%d bytes; il=%s testsDll=%s)" % (
            args.out, len(text), row["measured"]["il"], row["measured"]["testsDll"]))
    else:
        sys.stdout.write(text)
    return 0


if __name__ == "__main__":
    sys.exit(main())
