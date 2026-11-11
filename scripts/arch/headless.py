#!/usr/bin/env python3
"""Headless modules: the production modules declared to be rules, math and
data, and the gate that keeps their reach into the live game from growing.

`modules.toml` declares them under `[headless.modules]`, one reason each. The
measure is the game-coupling rule of ilanalysis.py ("can reach the game": a
method names a live game type or member, or calls a method that can, with
interface and virtual calls reaching every implementation in the assembly and
ParsekLog not followed). Pinning the methods that can reach the game would red
hundreds of names for one new read of a live helper, so the gate pins LEAKS
instead: the places where a headless method touches the live game itself.

A leak is one (headless method, live thing) pair, the method keyed by
`Type::Name` (overloads merge, generated code is folded into its owner):

- `game:<Type>`: the method names a live game type in its body or signature
  (a member of a non-inert type counts as its type; a live member of an inert
  type, such as `Quaternion::Euler`, is named as `game:<Type>::<member>`);
- `call:<Type>::<Name>`: the method calls a method outside the headless
  modules that can reach the game, or calls a slot declared outside them that
  a live implementation outside them fills (the slot is named, its
  implementations are in the details).

A callee INSIDE the headless modules adds no leak. If it can reach the game
itself it has a leak of its own further down the headless chain. If it is a
slot (an interface or virtual member) declared in a headless module that a
live implementation elsewhere fills, the call is a SEAM: the caller injects the
implementation, which is the shape the gate asks a leak to take. Seams are
counted and listed, not gated. `unexplained` checks that every live headless
method reaches a leak or a seam through headless calls, so the leak rule keeps
mirroring ilanalysis.game_reach.

The pins (`headless_pins.toml`, machine-written from a Debug build) are a SET
per module: a leak not pinned reds, a pinned leak that is gone passes and is
reported as slack until a re-pin. The pins also record the `[coupling]` lists
of facts.toml they were measured under and the declared module list, so the
DLL-free suite reds when either changes without a re-pin (widening the inert
list would otherwise make leaks vanish silently).

    python3 scripts/arch/headless.py Source/Parsek/bin/Debug/Parsek.dll              # report
    python3 scripts/arch/headless.py Source/Parsek/bin/Debug/Parsek.dll --write-pins # re-pin

Everything except `main` and the loaders is pure.
"""

from __future__ import annotations

import argparse
import fnmatch
import json
import re
import sys
import tomllib
from collections import Counter, defaultdict
from pathlib import Path

HERE = Path(__file__).resolve().parent
DEFAULT_MODULES = HERE / "modules.toml"
DEFAULT_PINS = HERE / "headless_pins.toml"
REPIN_COMMAND = (
    "python3 scripts/arch/headless.py Source/Parsek/bin/Debug/Parsek.dll --write-pins"
    " (after a Debug build of this tree; never from a Release build)"
)
# The [coupling] lists of facts.toml a leak depends on; the pins record them.
COUPLING_KEYS = ("game_assemblies", "inert_types", "live_members", "not_followed")
HEADLESS_KEYS = frozenset({"modules"})
NAMED = 12
PATH_HOPS = 4
CHOICES = (
    "Move the call behind an injected seam (an interface or a delegate the caller passes in) so the"
    " headless method stays pure; if the type really works with no game loaded, add it to [coupling]"
    " inert_types in scripts/arch/facts.toml with the reason (that is a re-pin too); if the leak is"
    " deliberate, re-pin and give the reason in the PR: "
)


# --- the declaration -------------------------------------------------------------------


def load_headless(path=DEFAULT_MODULES):
    """The raw `[headless]` table of a modules.toml ({} when it has none)."""
    with open(path, "rb") as handle:
        return tomllib.load(handle).get("headless", {})


def declared(table):
    """The declared module names, sorted ([] when the table is malformed)."""
    modules = table.get("modules") if isinstance(table, dict) else None
    return sorted(modules) if isinstance(modules, dict) else []


def declaration_problems(table, production, tooling=()):
    """Why `table` is not a usable `[headless]` declaration (empty when it is).

    It must name at least one module (an empty list would switch the gate
    off), each a production module of modules.toml with a reason; a tooling
    module, a name no rule declares or an unknown key reds.
    """
    if not isinstance(table, dict) or not table:
        return ["[headless] is missing: name the headless modules under [headless.modules], one reason each"]
    problems = ["[headless] has an unknown key %r (known: modules)" % key for key in sorted(set(table) - HEADLESS_KEYS)]
    modules = table.get("modules")
    if not isinstance(modules, dict) or not modules:
        return problems + ["[headless.modules] must map at least one module to the reason it is headless"]
    for name, reason in modules.items():
        if name in tooling:
            problems.append("[headless.modules] %s is a tooling module; only production modules are headless" % name)
        elif name not in production:
            problems.append("[headless.modules] %s names no module of modules.toml (renamed or a typo?)" % name)
        if not isinstance(reason, str) or not reason.strip():
            problems.append("[headless.modules] %s has no reason" % name)
    return problems


# --- the leaks ---------------------------------------------------------------------------


def method_name(key):
    """`Type::Name` of a method key: overloads and parameter changes merge."""
    path, _sep, rest = key.partition("::")
    return path + "::" + rest.split("(", 1)[0].split("``", 1)[0]


def _matches(text, patterns):
    return any(fnmatch.fnmatchcase(text, pattern) for pattern in patterns)


def game_thing(ref, coupling):
    """The leak target of a live game reference `asm|Type` or `asm|Type::member`."""
    _assembly, _sep, rest = ref.partition("|")
    path, sep, _member = rest.partition("::")
    if sep and _matches(path, coupling["inert_types"]):
        return "game:" + rest
    return "game:" + path


def _followed(key, not_followed):
    path = key.split("::", 1)[0]
    return not any(path == t or path.startswith(t + "/") for t in not_followed)


def live_path(key, direct_refs, via, limit=PATH_HOPS):
    """`A -> B -> game:X`: how a live method reaches the game (for messages only)."""
    hops = []
    seen = set()
    while key in via and key not in seen and len(hops) < limit:
        seen.add(key)
        key = via[key]
        hops.append(method_name(key))
    if key in direct_refs:
        hops.append(sorted(direct_refs[key])[0])
    elif hops:
        hops.append("...")
    return " -> ".join(hops)


def analyse(index, config, modules, direct=None, reach=None, via=None):
    """The headless report over one ilanalysis Index.

    `direct`, `reach` and `via` are ilanalysis.game_reach's results for the
    same index and config (computed here when not given). Returns JSON-ready
    {"modules", "leaks": {module: [leak keys]}, "details": {leak key: text},
    "byModule": {module: counts}, "unexplained": [method keys], "leakCount"}.
    """
    import ilanalysis  # noqa: PLC0415

    coupling = config["coupling"]
    not_followed = tuple(coupling.get("not_followed", ()))
    if direct is None or reach is None or via is None:
        via = {}
        direct, reach = ilanalysis.game_reach(index, config, via)
    headless = set(modules)
    methods = index.methods

    def is_headless(key):
        return key in methods and index.module(methods[key]["type"]) in headless

    memo, things = {}, {}
    direct_things = {}
    for key in direct:
        refs = ilanalysis.direct_game_refs(methods[key], coupling, memo)
        for ref in refs:
            if ref not in things:
                things[ref] = game_thing(ref, coupling)
        direct_things[key] = {things[ref] for ref in refs}

    leaks = defaultdict(set)
    seams = defaultdict(set)
    details = {}
    leaking = set()
    seamed = set()
    for key, entry in methods.items():
        if not is_headless(key):
            continue
        module = index.module(entry["type"])
        name = method_name(key)
        for thing in sorted(direct_things.get(key, ())):
            leak = "%s -> %s" % (name, thing)
            leaks[module].add(leak)
            details.setdefault(leak, "names %s" % thing[len("game:"):])
            leaking.add(key)
        for callee in entry["calls"]:
            if callee not in methods or not _followed(callee, not_followed):
                continue
            impls = sorted(i for i in index.implementers.get(callee, ())
                           if i in reach and _followed(i, not_followed) and not is_headless(i))
            if is_headless(callee):
                # A headless callee that can reach the game has a leak of its own; a
                # slot declared in a headless module is a seam whatever fills it.
                if impls:
                    seams[module].add("%s -> %s" % (name, method_name(callee)))
                    seamed.add(key)
                continue
            leak = "%s -> call:%s" % (name, method_name(callee))
            if callee in reach:
                leaks[module].add(leak)
                details.setdefault(leak, "calls %s, which reaches the game: %s"
                                   % (method_name(callee), live_path(callee, direct_things, via)))
                leaking.add(key)
            elif impls:
                leaks[module].add(leak)
                details.setdefault(leak, "calls the slot %s, which live %s fills: %s"
                                   % (method_name(callee), ", ".join(method_name(i) for i in impls[:3]),
                                      live_path(impls[0], direct_things, via)))
                leaking.add(key)

    # Every live headless method must reach a leaking method or a seam call
    # through headless calls (or slots), mirroring game_reach; anything else means
    # the leak rule no longer follows the coupling rule.
    explained = leaking | seamed
    pending = list(explained)
    while pending:
        key = pending.pop()
        if not _followed(key, not_followed):
            continue
        callers = set(index.callers.get(key, ()))
        for slot in index.slots.get(key, ()):
            if _followed(slot, not_followed):
                callers |= index.callers.get(slot, set())
        for caller in callers:
            if is_headless(caller) and caller not in explained:
                explained.add(caller)
                pending.append(caller)
    unexplained = sorted(k for k in reach if is_headless(k) and k not in explained)

    rows = defaultdict(Counter)
    for key, entry in methods.items():
        if not index.production(entry["type"]):
            continue
        row = rows[index.module(entry["type"])]
        n = ilanalysis.size(entry)
        row["methods"] += 1
        row["instructions"] += n
        if key in reach:
            row["reachMethods"] += 1
            row["reachInstructions"] += n
        if key in direct:
            row["directMethods"] += 1
            row["directInstructions"] += n
    by_module = {}
    for module, row in rows.items():
        out = {name: row.get(name, 0) for name in (
            "methods", "reachMethods", "directMethods", "instructions", "reachInstructions", "directInstructions")}
        out["headless"] = module in headless
        out["leaks"] = len(leaks.get(module, ())) if module in headless else None
        out["leakingMethods"] = (len({method_name(k) for k in leaking if index.module(methods[k]["type"]) == module})
                                 if module in headless else None)
        by_module[module] = out
    return {
        "modules": sorted(headless),
        "leaks": {module: sorted(leaks.get(module, ())) for module in sorted(headless)},
        "details": details,
        "byModule": dict(sorted(by_module.items(), key=lambda kv: (kv[1]["reachInstructions"]
                                                                    / (kv[1]["instructions"] or 1), kv[0]))),
        "seams": {module: sorted(seams.get(module, ())) for module in sorted(headless)},
        "unexplained": unexplained,
        "leakCount": sum(len(v) for v in leaks.values()),
        "seamCount": sum(len(v) for v in seams.values()),
    }


def thing_of(leak):
    return leak.split(" -> ", 1)[1]


def summary(result):
    """The numbers the metrics row, the Atlas and the --check section print."""
    if result is None:
        return None
    things = Counter(thing_of(leak) for keys in result["leaks"].values() for leak in keys)
    return {
        "modules": list(result["modules"]),
        "leaks": result["leakCount"],
        "leakingMethods": sum(row["leakingMethods"] or 0 for row in result["byModule"].values()),
        "seams": result["seamCount"],
        "byModule": {module: dict(row) for module, row in result["byModule"].items()},
        "topThings": [[thing, count] for thing, count in sorted(things.items(), key=lambda kv: (-kv[1], kv[0]))[:10]],
    }


# --- the ratchet -------------------------------------------------------------------------


def load_pins(path=DEFAULT_PINS):
    with open(path, "rb") as handle:
        return tomllib.load(handle)


def coupling_lists(config):
    """The [coupling] lists a leak depends on, as the pins record them."""
    coupling = config["coupling"]
    return {key: list(coupling.get(key, [])) for key in COUPLING_KEYS}


def render_pins(result, config, source="", date=""):
    lines = [
        "# The headless-module gate (scripts/arch/headless.py), read by",
        "# scripts/arch/test_ilheadless.py in CI's compiled-IL step. Machine-written:",
        "#   " + REPIN_COMMAND,
        "# One [[module]] per module of modules.toml [headless.modules]. `leaks` is a SET of",
        "# (headless method, live thing) pairs, the method as Type::Name: a leak not listed",
        "# reds; a listed leak that is gone passes and shows as slack until a re-pin. A rename",
        "# of a leaking method, or of the live method it calls, is a new leak: re-pin.",
        "# [coupling] records the facts.toml lists the leaks were measured under, and the",
        "# DLL-free suite reds when facts.toml differs or the declared modules change without",
        "# a re-pin, so widening the inert list is a deliberate diff here.",
        "# %d leaks in %d modules." % (result["leakCount"], len(result["modules"])),
    ]
    if source or date:
        lines.append("# Pinned from %s %s" % (source, date))
    lines.append("")
    lines.append("[coupling]")
    for key, values in coupling_lists(config).items():
        lines.append("%s = [" % key)
        lines.extend("    %s," % json.dumps(value) for value in values)
        lines.append("]")
    for module in result["modules"]:
        lines.append("")
        lines.append("[[module]]")
        lines.append("name = %s" % json.dumps(module))
        lines.append("leaks = [")
        lines.extend("    %s," % json.dumps(leak) for leak in result["leaks"][module])
        lines.append("]")
    return "\n".join(lines) + "\n"


def pinned(pins):
    """{module: set of leak keys} from a loaded pins file."""
    return {entry["name"]: set(entry.get("leaks", [])) for entry in pins.get("module", [])}


def pins_problems(pins, modules, config):
    """Why the pins no longer describe the declaration or the coupling lists. Needs no DLL."""
    problems = []
    names = [entry.get("name") for entry in pins.get("module", [])]
    if len(names) != len(set(names)):
        problems.append("headless_pins.toml lists a module twice")
    if sorted(set(names)) != sorted(modules):
        problems.append(
            "the pinned modules (%s) are not the declared [headless.modules] (%s): a module added to or"
            " removed from the declaration needs a re-pin: %s"
            % (", ".join(sorted(set(names))) or "none", ", ".join(sorted(modules)) or "none", REPIN_COMMAND))
    recorded = pins.get("coupling", {})
    for key, values in coupling_lists(config).items():
        if recorded.get(key) != values:
            before, after = set(recorded.get(key) or []), set(values)
            problems.append(
                "facts.toml [coupling] %s changed since the pin (added: %s; removed: %s): every leak"
                " depends on it, so re-pin to make the change visible in headless_pins.toml: %s"
                % (key, ", ".join(sorted(after - before)) or "none", ", ".join(sorted(before - after)) or "none",
                   REPIN_COMMAND))
    for entry in pins.get("module", []):
        leaks = entry.get("leaks", [])
        if len(leaks) != len(set(leaks)):
            problems.append("headless_pins.toml module %s lists a leak twice" % entry.get("name"))
        for leak in leaks:
            if not re.match(r"^[^ ]+::[^ ]+ -> (game|call):\S+$", leak):
                problems.append("headless_pins.toml module %s: %r is not `Type::Name -> game:X` or"
                                " `-> call:Type::Name`" % (entry.get("name"), leak))
    return problems


def _describe(leaks, details, limit=NAMED):
    text = "; ".join("%s (%s)" % (leak, details[leak]) if leak in details else leak for leak in leaks[:limit])
    if len(leaks) > limit:
        text += " +%d more" % (len(leaks) - limit)
    return text


def ratchet_problems(leaks, pins, details=None):
    """Why the measured leaks break the pins (empty when they hold): one problem per
    module with leaks the pins do not list."""
    details = details or {}
    table = pinned(pins)
    problems = []
    for module in sorted(leaks):
        new = sorted(set(leaks[module]) - table.get(module, set()))
        if new:
            problems.append(
                "headless module %s: %d new leak%s (a headless method touching the live game): %s. %s%s"
                % (module, len(new), "" if len(new) == 1 else "s", _describe(new, details), CHOICES, REPIN_COMMAND))
    return problems


def ratchet_slack(leaks, pins):
    """Pinned leaks that are gone (a re-pin would drop them)."""
    out = []
    for module, keys in sorted(pinned(pins).items()):
        gone = sorted(keys - set(leaks.get(module, ())))
        if gone:
            out.append("%s: %d pinned leak%s gone: %s" % (module, len(gone), "" if len(gone) == 1 else "s",
                                                          _describe(gone, {}, 4)))
    return out


# --- report -------------------------------------------------------------------------------


def _pct(part, whole):
    return "%3.0f%%" % (100.0 * part / (whole or 1))


def table_lines(by_module):
    """Per production module: methods that can reach the game, directly and only
    through a callee, and the same shares of instructions."""
    lines = ["  %-13s %6s %6s %6s %6s %9s %6s %6s %6s" % (
        "module", "methods", "reach", "direct", "callee", "instr", "reach", "direct", "leaks")]
    for module, row in by_module.items():
        lines.append("  %-13s %6d %6s %6s %6s %9d %6s %6s %6s" % (
            module + (" *" if row["headless"] else ""), row["methods"],
            _pct(row["reachMethods"], row["methods"]), _pct(row["directMethods"], row["methods"]),
            _pct(row["reachMethods"] - row["directMethods"], row["methods"]), row["instructions"],
            _pct(row["reachInstructions"], row["instructions"]),
            _pct(row["directInstructions"], row["instructions"]),
            "-" if row["leaks"] is None else row["leaks"]))
    return lines


def format_report(result, top=10):
    head = summary(result)
    lines = ["HEADLESS MODULES (%s; * = declared in modules.toml [headless.modules])" % ", ".join(result["modules"]),
             "  can reach the game, per module: share of methods (and of their instructions) that can;"
             " direct = the method names a live game type or member itself; callee = only through a call"]
    lines.extend(table_lines(result["byModule"]))
    lines.append("  leaks: %d (%d methods); the live things leaked to most:" % (head["leaks"], head["leakingMethods"]))
    for thing, count in head["topThings"][:top]:
        lines.append("    %4d  %s" % (count, thing))
    lines.append("  seam calls (a headless method calling a slot declared in a headless module that a live"
                 " implementation elsewhere fills; reported, not gated): %d" % head["seams"])
    if result["unexplained"]:
        lines.append("  PROBLEM live headless methods no leak explains (the leak rule no longer mirrors"
                     " ilanalysis.game_reach): %s" % ", ".join(result["unexplained"][:NAMED]))
    return lines


def main(argv=None):
    parser = argparse.ArgumentParser(description="Headless modules and their leak gate over the compiled IL.")
    parser.add_argument("input", help="Parsek.dll, an ikdasm .il dump or an ilfacts.py --json file")
    parser.add_argument("--write-pins", action="store_true", help="re-pin headless_pins.toml from this input")
    parser.add_argument("--pins", default=str(DEFAULT_PINS))
    parser.add_argument("--modules", default=str(DEFAULT_MODULES))
    parser.add_argument("--leaks", action="store_true", help="list every leak with its details")
    args = parser.parse_args(argv)
    if args.write_pins and re.search(r"[\\/]Release[\\/]", str(args.input)):
        parser.error("pin from the Debug build CI measures, not a Release build")
    sys.path.insert(0, str(HERE))
    import ilanalysis  # noqa: PLC0415
    import layers  # noqa: PLC0415

    table = load_headless(args.modules)
    production, tooling = layers.declared_modules(args.modules)
    shape = declaration_problems(table, production, tooling)
    for problem in shape:
        print("DECLARATION " + problem)
    config = ilanalysis.load_config()
    facts, tooling_names = ilanalysis.load_facts(args.input, args.modules)
    result = analyse(ilanalysis.Index(facts, tooling_names), config, declared(table))
    print("\n".join(format_report(result)))
    if args.leaks:
        for module in result["modules"]:
            for leak in result["leaks"][module]:
                print("    %s  [%s]" % (leak, result["details"].get(leak, "")))
            for seam in result["seams"][module]:
                print("    seam %s" % seam)
    if args.write_pins:
        if shape or result["unexplained"]:
            parser.error("fix the problems above before pinning")
        import datetime  # noqa: PLC0415

        Path(args.pins).write_text(render_pins(result, config, Path(args.input).name,
                                               datetime.date.today().isoformat()), encoding="utf-8", newline="\n")
        print("Wrote %s" % args.pins)
        return 0
    problems = []
    if Path(args.pins).is_file():
        pins = load_pins(args.pins)
        problems = pins_problems(pins, declared(table), config) + ratchet_problems(
            result["leaks"], pins, result["details"])
        for line in ratchet_slack(result["leaks"], pins):
            print("  slack (re-pin to tighten): " + line)
    for line in problems or ["the headless leaks hold their pins"]:
        print("  " + line)
    return 1 if problems or shape or result["unexplained"] else 0


if __name__ == "__main__":
    sys.exit(main())
