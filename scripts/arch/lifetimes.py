#!/usr/bin/env python3
"""Declared lifetimes of the mutable static state, and the gates that read them.

`lifetimes.toml` gives the reassignable statics and the mutated static readonly
collections of Parsek.dll production code (the population
`ilanalysis.static_state` reports) a lifetime: process, save, load-transition,
scene, scoped or test-hook. Tagging is honest, not complete: a static nobody
has shown the lifetime of stays untagged, and the untagged set is pinned in
`lifetime_pins.toml`, so only a NEW untagged static reds.

Four findings turn the tags into checks, each read from the compiled IL:

- coldLoadMisses: a `save` or `load-transition` static with no CLEAR inside
  the transitive call closure of the Cold load roots the declaration names (a
  clear is an `stsfld`, a call to its auto-property setter, `ldsflda` then
  `initobj`, or a `[cold_load.clearing_members]` call whose receiver is the
  static's own value; an append, an element store or a read is not), and no
  verified `[cold_load.cleared_by]` entry: the static form of "state survived
  a load it should not have".
- testHookWrittenByProduction: a `test-hook` static written by another
  production type (tooling and methods named as test hooks do not count).
- testWrittenUntagged: a static Parsek.Tests.dll writes directly (`stsfld`, or
  its auto-property setter) that has no lifetime (clrmeta.py reads the test
  assembly's metadata in about a second, no disassembly).
- resetGateDisagreement: a static of a SaveScopedSessionResetTests type that
  ResetSaveScopedSessionState resets (not in scripts/session-reset-allowlist.txt)
  but is not tagged save or load-transition.

Each list is pinned with facts_pins.toml's contract (a count that may not
rise; a swap passes; slack is reported until a re-pin). The declaration
itself is checked without a DLL (shape, the reset gate's types and allowlist)
and, with one, against the assembly (every named type, field and root exists).

    python3 scripts/arch/lifetimes.py Source/Parsek/bin/Debug/Parsek.dll            # report
    python3 scripts/arch/lifetimes.py Source/Parsek/bin/Debug/Parsek.dll --write-pins

The report reads the test assembly from its build path (or `--tests`); without
it testWrittenUntagged is not measured. Everything except `main` and the
loaders is pure.
"""

from __future__ import annotations

import argparse
import fnmatch
import json
import re
import sys
import tomllib
from collections import Counter
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO_ROOT = HERE.parent.parent
DEFAULT_DECLARATION = HERE / "lifetimes.toml"
DEFAULT_PINS = HERE / "lifetime_pins.toml"
DEFAULT_TESTS_DLL = REPO_ROOT / "Source" / "Parsek.Tests" / "bin" / "Debug" / "net472" / "Parsek.Tests.dll"
RESET_GATE_TESTS = REPO_ROOT / "Source" / "Parsek.Tests" / "SaveScopedSessionResetTests.cs"
RESET_GATE_ALLOWLIST = REPO_ROOT / "scripts" / "session-reset-allowlist.txt"
RESET_GATE_ARRAY = "InventoryTypes"
REPIN_COMMAND = (
    "python3 scripts/arch/lifetimes.py Source/Parsek/bin/Debug/Parsek.dll --write-pins"
    " (after a Debug build of this tree; never from a Release build)"
)
# The lifetimes a Cold load must clear; the gates read these two and test-hook.
COLD_CLEARED = ("save", "load-transition")
TEST_HOOK = "test-hook"
REQUIRED_LIFETIMES = COLD_CLEARED + (TEST_HOOK,)
FINDINGS = ("coldLoadMisses", "testHookWrittenByProduction", "testWrittenUntagged", "resetGateDisagreement")
FINDING_LABELS = {
    "coldLoadMisses": "save / load-transition statics no Cold load path clears",
    "testHookWrittenByProduction": "test-hook statics another production type writes",
    "testWrittenUntagged": "untagged statics the test assembly writes directly",
    "resetGateDisagreement": "statics ResetSaveScopedSessionState resets that are not tagged save or load-transition",
}
TOP_KEYS = frozenset({"lifetimes", "cold_load", "pattern", "type"})
PATTERN_KEYS = frozenset({"match", "lifetime", "reason"})
TYPE_KEYS = frozenset({"name", "default", "reason", "fields"})
COLD_LOAD_KEYS = frozenset({"roots", "clearing_members", "cleared_by"})
CLEARED_BY_KEYS = frozenset({"method", "reason"})
FIELD_KEY_RE = re.compile(r"^[\w.`/<>]+::[\w.`<>$]+$")
METHOD_KEY_RE = re.compile(r"^[\w.`/<>]+::[\w.`<>]+\(.*\)$")
NAMED = 12


# --- the declaration -------------------------------------------------------------------


def load_declaration(path=DEFAULT_DECLARATION):
    with open(path, "rb") as handle:
        return tomllib.load(handle)


def split_entry(value):
    """`"save: reason"` -> ("save", "reason"); `"test-hook"` -> ("test-hook", "")."""
    if not isinstance(value, str):
        return None, ""
    lifetime, _sep, reason = value.partition(":")
    return lifetime.strip(), reason.strip()


def field_display_name(name):
    """The name a declaration uses: a backing field `<P>k__BackingField` is `P`."""
    match = re.match(r"^<([^>]+)>k__BackingField$", name)
    return match.group(1) if match else name


def declaration_problems(decl):
    """Why a declaration is malformed (empty when it is well-formed). Needs no DLL."""
    problems = []
    lifetimes = decl.get("lifetimes")
    if not isinstance(lifetimes, dict) or not lifetimes:
        return ["[lifetimes] must name each lifetime with its meaning"]
    known = set(lifetimes)
    for key in sorted(set(decl) - TOP_KEYS):
        problems.append("unknown top-level key %r (known: %s)" % (key, ", ".join(sorted(TOP_KEYS))))
    for name in REQUIRED_LIFETIMES:
        if name not in known:
            problems.append("[lifetimes] lacks %s, which the gates read" % name)
    for name, meaning in lifetimes.items():
        if not isinstance(meaning, str) or not meaning.strip():
            problems.append("[lifetimes] %s has no meaning" % name)

    def check_lifetime(value, where):
        if value not in known:
            problems.append("%s: unknown lifetime %r (known: %s)" % (where, value, ", ".join(sorted(known))))

    cold = decl.get("cold_load") or {}
    for key in sorted(set(cold) - COLD_LOAD_KEYS):
        problems.append("[cold_load] has an unknown key %r (known: %s)" % (key, ", ".join(sorted(COLD_LOAD_KEYS))))
    clearing = cold.get("clearing_members")
    if not isinstance(clearing, dict) or not clearing:
        problems.append("[cold_load.clearing_members] must name at least one member with its reason")
    else:
        for member, reason in clearing.items():
            if not isinstance(reason, str) or not reason.strip():
                problems.append("[cold_load.clearing_members] %s has no reason" % member)
    for field, entry in (cold.get("cleared_by") or {}).items():
        where = "[cold_load.cleared_by] %s" % field
        if not FIELD_KEY_RE.match(field):
            problems.append("%s is not a field key `Type::name`" % where)
        if not isinstance(entry, dict):
            problems.append("%s must be a table with method and reason" % where)
            continue
        for key in sorted(set(entry) - CLEARED_BY_KEYS):
            problems.append("%s has an unknown key %r" % (where, key))
        if not METHOD_KEY_RE.match(str(entry.get("method", ""))):
            problems.append("%s: method %r is not a method key `Type::Name(params)`" % (where, entry.get("method")))
        if not str(entry.get("reason", "")).strip():
            problems.append("%s has no reason" % where)
    roots = cold.get("roots")
    if not isinstance(roots, dict) or not roots:
        problems.append("[cold_load.roots] must name at least one method key with its reason")
    else:
        for key, reason in roots.items():
            if not METHOD_KEY_RE.match(key):
                problems.append("[cold_load.roots] %r is not a method key `Type::Name(params)`" % key)
            if not isinstance(reason, str) or not reason.strip():
                problems.append("[cold_load.roots] %s has no reason" % key)
    for index, pattern in enumerate(decl.get("pattern", [])):
        where = "[[pattern]] #%d" % (index + 1)
        for key in sorted(set(pattern) - PATTERN_KEYS):
            problems.append("%s has an unknown key %r" % (where, key))
        if not isinstance(pattern.get("match"), str) or not pattern.get("match"):
            problems.append("%s has no match" % where)
        check_lifetime(pattern.get("lifetime"), where)
        if not str(pattern.get("reason", "")).strip():
            problems.append("%s has no reason" % where)
    seen_types = set()
    for entry in decl.get("type", []):
        name = entry.get("name")
        where = "[[type]] %s" % name
        if not isinstance(name, str) or not name:
            problems.append("a [[type]] has no name")
            continue
        if name in seen_types:
            problems.append("%s is declared twice" % where)
        seen_types.add(name)
        for key in sorted(set(entry) - TYPE_KEYS):
            problems.append("%s has an unknown key %r (a typo leaves its statics untagged)" % (where, key))
        if "default" in entry:
            check_lifetime(entry["default"], where + " default")
        if not str(entry.get("reason", "")).strip():
            problems.append("%s has no reason" % where)
        fields = entry.get("fields", {})
        if "default" not in entry and not fields:
            problems.append("%s tags nothing (no default, no fields)" % where)
        names = {}
        for field, value in fields.items():
            lifetime, _reason = split_entry(value)
            check_lifetime(lifetime, "%s field %s" % (where, field))
            display = field_display_name(field)
            if display in names:
                problems.append("%s tags field %s twice (as %s and %s)" % (where, display, names[display], field))
            names[display] = field
    return problems


def type_entries(decl):
    return {entry["name"]: entry for entry in decl.get("type", []) if isinstance(entry.get("name"), str)}


def resolve(decl, keys, fields):
    """{field key: (lifetime, source, reason)} for the tagged keys, and the untagged keys.

    `keys` are the population (`Type::name`); `fields` the facts fields (for
    the backing-field property name). Source is "field", "pattern" or "type".
    """
    entries = type_entries(decl)
    patterns = decl.get("pattern", [])
    tags = {}
    untagged = []
    for key in sorted(keys):
        path, name = key.split("::", 1)
        display = (fields.get(key) or {}).get("backingFor") or field_display_name(name)
        entry = entries.get(path, {})
        own = entry.get("fields", {})
        value = own.get(display, own.get(name))
        if value is not None:
            lifetime, reason = split_entry(value)
            tags[key] = (lifetime, "field", reason or entry.get("reason", ""))
            continue
        pattern = next((p for p in patterns if fnmatch.fnmatchcase(display, p["match"])), None)
        if pattern is not None:
            tags[key] = (pattern["lifetime"], "pattern", pattern["reason"])
            continue
        if "default" in entry:
            tags[key] = (entry["default"], "type", entry.get("reason", ""))
            continue
        untagged.append(key)
    return tags, untagged


def assembly_problems(decl, facts):
    """Why the declaration does not match the assembly: a type, field or root it names is gone."""
    problems = []
    statics = {}
    for key, field in facts["fields"].items():
        if field["static"] and not field["literal"]:
            path, name = key.split("::", 1)
            statics.setdefault(path, set()).update({name, field.get("backingFor") or name})
    for name, entry in type_entries(decl).items():
        if name not in facts["types"]:
            problems.append("[[type]] %s names no type in the assembly (renamed or deleted? edit lifetimes.toml)"
                            % name)
            continue
        for field in entry.get("fields", {}):
            if field not in statics.get(name, set()) and field_display_name(field) not in statics.get(name, set()):
                problems.append("[[type]] %s tags field %s, which is no static field of that type"
                                " (renamed or deleted? edit lifetimes.toml)" % (name, field))
    for root in (decl.get("cold_load") or {}).get("roots", {}):
        if root not in facts["methods"]:
            problems.append("[cold_load.roots] %s is no method in the assembly (edit lifetimes.toml)" % root)
    return problems


# --- the reset gate (SaveScopedSessionResetTests + its allowlist) ------------------------


def parse_inventory_types(cs_text, array=RESET_GATE_ARRAY):
    """The `typeof(X)` items of the C# array initialiser named `array`, comments and
    strings removed first, so a commented-out line is not read as a type."""
    import archview  # noqa: PLC0415

    code = archview.strip_comments_and_strings(cs_text)
    match = re.search(r"\b%s\s*=\s*(?:new\s+[\w.]+\s*\[\s*\]\s*)?\{" % re.escape(array), code)
    if not match:
        raise ValueError("no `%s = { ... }` initialiser" % array)
    depth = 1
    pos = match.end()
    while depth and pos < len(code):
        depth += {"{": 1, "}": -1}.get(code[pos], 0)
        pos += 1
    body = code[match.end(): pos - 1]
    names = []
    for item in (part.strip() for part in body.split(",")):
        if not item:
            continue
        typeof = re.fullmatch(r"typeof\s*\(\s*([\w.]+)\s*\)", item)
        if not typeof:
            raise ValueError("%s item %r is not typeof(Type)" % (array, item))
        names.append(typeof.group(1))
    return names


def il_path_of(csharp_name):
    """A C# type name from the test file to the facts' path (the file's namespace is Parsek.Tests,
    so an unqualified name resolves in Parsek)."""
    return csharp_name if csharp_name.startswith("Parsek.") else "Parsek." + csharp_name


def parse_allowlist(text):
    """{`Type.field`: reason} from session-reset-allowlist.txt (the C# gate's own format)."""
    out = {}
    for raw in text.splitlines():
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        key, _sep, reason = line.partition(" ")
        out[key] = reason.strip()
    return out


def load_reset_gate(tests_path=RESET_GATE_TESTS, allowlist_path=RESET_GATE_ALLOWLIST):
    types = [il_path_of(name) for name in parse_inventory_types(Path(tests_path).read_text(encoding="utf-8"))]
    return {"types": types, "allowlist": parse_allowlist(Path(allowlist_path).read_text(encoding="utf-8"))}


def short_name(path):
    return path.split("/")[-1].rsplit(".", 1)[-1]


def reset_gate_problems(decl, gate):
    """Where the declaration contradicts the C# reset gate. Needs no DLL.

    Every gated type is declared with a default a Cold load clears; every
    allowlisted field has a field entry (its reason states a lifetime); a field
    entry in a gated type that a Cold load does not clear names an allowlisted
    field (the helper resets every other one)."""
    problems = []
    entries = type_entries(decl)
    by_short = {}
    for path in gate["types"]:
        by_short.setdefault(short_name(path), []).append(path)
        entry = entries.get(path)
        if entry is None:
            problems.append("%s is a SaveScopedSessionResetTests type with no [[type]] entry in lifetimes.toml"
                            % path)
        elif entry.get("default") not in COLD_CLEARED:
            problems.append("%s: the reset helper resets its statics, so its default must be save or"
                            " load-transition, not %r" % (path, entry.get("default")))
    allowed = {}
    for key in gate["allowlist"]:
        type_name, _sep, field = key.partition(".")
        paths = by_short.get(type_name, [])
        if len(paths) != 1:
            problems.append("allowlist entry %s names no single SaveScopedSessionResetTests type" % key)
            continue
        allowed.setdefault(paths[0], set()).add(field)
        if field not in entries.get(paths[0], {}).get("fields", {}):
            problems.append("allowlisted %s has no field entry under [[type]] %s (tag the lifetime its"
                            " allowlist reason states)" % (key, paths[0]))
    for path in gate["types"]:
        for field, value in entries.get(path, {}).get("fields", {}).items():
            lifetime, _reason = split_entry(value)
            if lifetime not in COLD_CLEARED and field not in allowed.get(path, set()):
                problems.append("%s.%s is tagged %s, but it is not allowlisted, so the reset helper resets it"
                                % (short_name(path), field, lifetime))
    return problems


# --- the IL side -------------------------------------------------------------------------


def cold_closure(index, roots):
    """Methods the roots reach: calls, and every implementation or override of a called slot."""
    seen = set()
    pending = [root for root in roots if root in index.methods]
    while pending:
        key = pending.pop()
        if key in seen:
            continue
        seen.add(key)
        for callee in index.methods[key]["calls"]:
            if callee not in seen:
                pending.append(callee)
            pending.extend(impl for impl in index.implementers.get(callee, ()) if impl not in seen)
    return seen


def static_setters(index):
    """{(type path, property): static setter key}."""
    out = {}
    for key, entry in index.methods.items():
        if entry["name"].startswith("set_") and "static" in entry["flags"]:
            out[(entry["type"], entry["name"][4:])] = key
    return out


def clears_of(index, key, clearing, setters):
    """Every method that can empty or replace a static's value: `stsfld`, its
    auto-property setter's callers, `ldsflda` + `initobj`, and a clearing call
    whose receiver is the static's own value (`directReceiverCalls`)."""
    field = index.fields[key]
    path = key.split("::", 1)[0]
    out = set(field.get("writers", ())) | set(field.get("defaultedBy", ()))
    if field.get("backingFor"):
        setter = setters.get((path, field["backingFor"]))
        if setter is not None:
            out |= index.callers.get(setter, set())
    for call in field.get("directReceiverCalls", ()):
        method, _sep, member = call.rpartition("|")
        if member in clearing:
            out.add(method)
    return out


def cleared_by_problems(index, key, entry, closure):
    """Why a `[cold_load.cleared_by]` entry does not show a Cold clear (empty when it does)."""
    method = entry.get("method")
    field = index.fields.get(key)
    if field is None:
        return ["[cold_load.cleared_by] %s names no static field in the assembly" % key]
    if method not in index.methods:
        return ["[cold_load.cleared_by] %s: %s is no method in the assembly" % (key, method)]
    if method not in closure:
        return ["[cold_load.cleared_by] %s: %s is not in the Cold closure" % (key, method)]
    touching = set(field.get("readers", ())) | set(field.get("writers", ())) | set(field.get("addressTakenBy", ()))
    # The named method itself must touch the static. "Called by a reader" would
    # accept any helper a reader happens to call (a logger), so an entry could
    # go stale unseen; when the clear is a helper handed the static, name the
    # caller that passes it.
    if method in touching:
        return []
    return ["[cold_load.cleared_by] %s: %s does not read, write or take the address of the static"
            " (name the closure method that touches it)" % (key, method)]


def writes_of(index, key, mutating, setters):
    """Every method that can change a static's value: `stsfld`, its auto-property
    setter's callers, `ldsflda` + `initobj`, and mutating calls on the collection."""
    field = index.fields[key]
    path = key.split("::", 1)[0]
    out = set(field.get("writers", ())) | set(field.get("defaultedBy", ()))
    if field.get("backingFor"):
        setter = setters.get((path, field["backingFor"]))
        if setter is not None:
            out |= index.callers.get(setter, set())
    for call in field.get("receiverCalls", ()):
        method, _sep, member = call.rpartition("|")
        if member in mutating:
            out.add(method)
    return out


def test_writes(rows, facts, assembly="Parsek"):
    """{field key: [test types]} the test assembly writes directly, from clrmeta.static_access rows:
    an `stsfld`, or a call to a static property setter (the statics that setter's own body
    stores: an auto-property's backing field, or the field a hand-written setter assigns)."""
    out = {}
    by_setter = {}
    for key, field in facts["fields"].items():
        path = key.split("::", 1)[0]
        if field.get("backingFor"):
            by_setter.setdefault((path, "set_" + field["backingFor"]), set()).add(key)
        for writer in field.get("writers", ()):
            owner, _sep, member = writer.partition("::")
            name = member.split("(", 1)[0]
            if name.startswith("set_"):
                by_setter.setdefault((owner, name), set()).add(key)
    for writer, opcode, target in rows:
        owner, _sep, member = target.partition("|")
        if owner != assembly:
            continue
        path, _sep, name = member.partition("::")
        if opcode == "stsfld":
            keys = {member}
        elif opcode in ("call", "callvirt") and name.startswith("set_"):
            keys = by_setter.get((path, name), set())
        else:
            continue
        for key in keys:
            if key in facts["fields"]:
                out.setdefault(key, set()).add(writer or "?")
    return {key: sorted(writers) for key, writers in out.items()}


def analyse(index, statics, config, decl, gate=None, tests_written=None):
    """The lifetime report over one ilanalysis Index and its static_state result.

    `gate` is load_reset_gate's result (None skips resetGateDisagreement);
    `tests_written` is test_writes' result (None: testWrittenUntagged is not
    measured and stays None).
    """
    import ilanalysis  # noqa: PLC0415
    import ilfacts  # noqa: PLC0415

    rows = statics["fields"]
    population = sorted(k for k, row in rows.items() if row["reassignable"] or row["mutated"])
    tags, untagged = resolve(decl, population, index.fields)
    roots = list((decl.get("cold_load") or {}).get("roots", {}))
    closure = cold_closure(index, roots)
    mutating = set(config["statics"]["mutating_members"])
    cold = decl.get("cold_load") or {}
    clearing = set(cold.get("clearing_members") or {})
    cleared_by = cold.get("cleared_by") or {}
    setters = static_setters(index)
    suffixes = config["uncalled"]["test_hook_suffixes"]
    details = {}

    misses = []
    problems = []
    for key, (lifetime, _source, _reason) in tags.items():
        if lifetime not in COLD_CLEARED:
            continue
        clears = clears_of(index, key, clearing, setters)
        declared_ok = False
        if key in cleared_by:
            found = cleared_by_problems(index, key, cleared_by[key], closure)
            problems.extend(found)
            declared_ok = not found
        if clears & closure or declared_ok:
            continue
        misses.append(key)
        named = sorted(clears)
        details[key] = "%s; cleared only by %s" % (lifetime, ", ".join(named[:3]) + (" ..." if len(named) > 3
                                                                                      else "")) \
            if named else "%s; never cleared (appends and reads do not clear)" % lifetime
    for key in sorted(set(cleared_by) - {k for k, v in tags.items() if v[0] in COLD_CLEARED}):
        problems.append("[cold_load.cleared_by] %s is not a tagged save or load-transition static" % key)

    hooks = []
    for key, (lifetime, _source, _reason) in tags.items():
        if lifetime != TEST_HOOK:
            continue
        own = ilfacts.outermost(key.split("::", 1)[0])
        outside = sorted(
            w for w in writes_of(index, key, mutating, setters)
            if w in index.methods and index.production(index.methods[w]["type"])
            and ilfacts.outermost(index.methods[w]["type"]) != own and not ilanalysis.is_test_hook(w, suffixes))
        if outside:
            hooks.append(key)
            details[key] = "test-hook written by %s" % ", ".join(outside[:3])

    written = None
    if tests_written is not None:
        untagged_set = set(untagged)
        written = sorted(k for k in tests_written if k in untagged_set)
        for key in written:
            details.setdefault(key, "untagged; the test assembly writes it (%s)"
                               % ", ".join(tests_written[key][:2]))

    disagreement = None
    if gate is not None:
        disagreement = []
        gated = set(gate["types"])
        allowed = set()
        for entry in gate["allowlist"]:
            type_name, _sep, field = entry.partition(".")
            allowed.add((type_name, field))
        for key in population:
            path, name = key.split("::", 1)
            if path not in gated:
                continue
            display = index.fields[key].get("backingFor") or name
            if (short_name(path), display) in allowed:
                continue
            lifetime = tags.get(key, (None,))[0]
            if lifetime not in COLD_CLEARED:
                disagreement.append(key)
                details[key] = "reset by the helper, tagged %s" % lifetime

    by_lifetime = Counter(lifetime for lifetime, _s, _r in tags.values())
    by_source = Counter(source for _l, source, _r in tags.values())
    findings = {
        "coldLoadMisses": sorted(misses),
        "testHookWrittenByProduction": sorted(hooks),
        "testWrittenUntagged": written,
        "resetGateDisagreement": sorted(disagreement) if disagreement is not None else None,
    }
    return {
        "population": len(population),
        "tagged": {key: list(value) for key, value in tags.items()},
        "untagged": untagged,
        "byLifetime": {name: by_lifetime.get(name, 0) for name in decl.get("lifetimes", {})},
        "bySource": dict(by_source),
        "coldRoots": roots,
        "coldClosure": len(closure),
        "findings": findings,
        "details": details,
        "problems": assembly_problems(decl, index.facts) + problems,
        "gates": dict({"untagged": list(untagged)}, **{name: keys for name, keys in findings.items()}),
    }


def summary(result):
    """The numbers the metrics row, the Atlas and the report print."""
    return {
        "population": result["population"],
        "tagged": result["population"] - len(result["untagged"]),
        "untagged": len(result["untagged"]),
        "byLifetime": dict(result["byLifetime"]),
        "coldClosureMethods": result["coldClosure"],
        "findings": {name: (len(keys) if keys is not None else None) for name, keys in result["findings"].items()},
    }


# --- the ratchet -------------------------------------------------------------------------


def load_pins(path=DEFAULT_PINS):
    with open(path, "rb") as handle:
        return tomllib.load(handle)


def render_pins(gates, source="", date=""):
    lines = [
        "# The static-lifetime gates (scripts/arch/lifetimes.py), read by",
        "# scripts/arch/test_static_lifetimes.py in CI's compiled-IL step. Machine-written:",
        "#   " + REPIN_COMMAND,
        "# [untagged] is a SET: a static with no lifetime in lifetimes.toml that is not listed",
        "# here reds (tag it; a renamed static is a new one); a listed one that is tagged or deleted passes. The",
        "# finding lists are COUNTED like facts_pins.toml: the measured list may not outnumber",
        "# `bound`; a swap inside the bound passes, and slack after a fall is not enforced until",
        "# a re-pin, so re-pin in the PR that lowers a count and raise one only for a change",
        "# the PR explains.",
    ]
    if source or date:
        lines.append("# Pinned from %s %s" % (source, date))
    lines.append("")
    lines.append("[untagged]")
    lines.append("keys = [")
    lines.extend("    %s," % json.dumps(key) for key in sorted(gates["untagged"]))
    lines.append("]")
    for name in FINDINGS:
        keys = gates.get(name)
        if keys is None:
            raise ValueError("%s was not measured; pin with the test assembly and the reset gate" % name)
        lines.append("")
        lines.append("[%s]" % name)
        lines.append("bound = %d" % len(keys))
        lines.append("keys = [")
        lines.extend("    %s," % json.dumps(key) for key in sorted(keys))
        lines.append("]")
    return "\n".join(lines) + "\n"


def _describe(keys, details, limit=NAMED):
    text = "; ".join("%s%s" % (key, (" (" + details[key] + ")") if key in details else "") for key in keys[:limit])
    if len(keys) > limit:
        text += " +%d more" % (len(keys) - limit)
    return text or "none"


def ratchet_problems(gates, pins, details=None, names=None):
    """Why the measured gates break the pins (empty when they hold). A finding that
    was not measured (None) is skipped; `names` limits the check."""
    details = details or {}
    problems = []
    if names is None or "untagged" in names:
        new = sorted(set(gates["untagged"]) - set(pins["untagged"]["keys"]))
        if new:
            problems.append(
                "statics with no declared lifetime (%d new): %s. Give each a lifetime in"
                " scripts/arch/lifetimes.toml (a field entry, a pattern or its type's default);"
                " if leaving one untagged is deliberate, re-pin: %s" % (len(new), _describe(new, details),
                                                                        REPIN_COMMAND))
    for name in FINDINGS:
        if names is not None and name not in names:
            continue
        measured = gates.get(name)
        if measured is None:
            continue
        pin = pins[name]
        if len(measured) > pin["bound"]:
            new = sorted(set(measured) - set(pin["keys"]))
            gone = sorted(set(pin["keys"]) - set(measured))
            problems.append(
                "%s: %d, pinned bound %d (+%d). New since the pin: %s.%s If this is deliberate, re-pin: %s"
                % (FINDING_LABELS[name], len(measured), pin["bound"], len(measured) - pin["bound"],
                   _describe(new, details), (" Gone since the pin: %s." % "; ".join(gone)) if gone else "",
                   REPIN_COMMAND))
    return problems


def ratchet_slack(gates, pins):
    """What a re-pin would tighten."""
    out = []
    gone = set(pins["untagged"]["keys"]) - set(gates["untagged"])
    if gone:
        out.append("untagged: %d pinned statics are now tagged or gone" % len(gone))
    for name in FINDINGS:
        measured = gates.get(name)
        if measured is not None and len(measured) < pins[name]["bound"]:
            out.append("%s: %d below the bound %d" % (name, len(measured), pins[name]["bound"]))
    return out


# --- report -------------------------------------------------------------------------------


def format_report(result, top=8):
    head = summary(result)
    lines = ["STATIC LIFETIMES (%d mutable statics: %d tagged, %d untagged)"
             % (head["population"], head["tagged"], head["untagged"])]
    lines.append("  " + ", ".join("%s %d" % (name, n) for name, n in head["byLifetime"].items()))
    lines.append("  tagged by a field entry %d, a pattern %d, a type default %d"
                 % (result["bySource"].get("field", 0), result["bySource"].get("pattern", 0),
                    result["bySource"].get("type", 0)))
    lines.append("  Cold load closure: %d methods from %s" % (result["coldClosure"], ", ".join(result["coldRoots"])))
    for name in FINDINGS:
        keys = result["findings"][name]
        if keys is None:
            lines.append("  %s: not measured" % name)
            continue
        lines.append("  %s (%s): %d" % (name, FINDING_LABELS[name], len(keys)))
        for key in keys[:top]:
            lines.append("    %s%s" % (key, ("  " + result["details"][key]) if key in result["details"] else ""))
        if len(keys) > top:
            lines.append("    +%d more" % (len(keys) - top))
    for problem in result["problems"]:
        lines.append("  PROBLEM " + problem)
    return lines


def analyse_facts(facts, tooling, tests_dll=None, decl_path=DEFAULT_DECLARATION, config=None):
    """The lifetime report over facts already built, with the test assembly when it is given and exists."""
    import clrmeta  # noqa: PLC0415
    import ilanalysis  # noqa: PLC0415

    config = config or ilanalysis.load_config()
    index = ilanalysis.Index(facts, tooling)
    statics = ilanalysis.static_state(index, config)
    written = None
    if tests_dll is not None and Path(tests_dll).is_file():
        rows, _skipped = clrmeta.static_access(clrmeta.read_assembly(tests_dll))
        written = test_writes(rows, facts, facts.get("assembly") or "Parsek")
    return analyse(index, statics, config, load_declaration(decl_path), load_reset_gate(), written)


def analyse_paths(dll, tests_dll=None, decl_path=DEFAULT_DECLARATION, modules=None, source=None):
    """(result, facts) from an assembly path, with the test assembly when it is given and exists."""
    import archview  # noqa: PLC0415
    import ilanalysis  # noqa: PLC0415

    facts, tooling = ilanalysis.load_facts(dll, modules or archview.DEFAULT_MODULES, source)
    return analyse_facts(facts, tooling, tests_dll, decl_path), facts


def main(argv=None):
    parser = argparse.ArgumentParser(description="Declared static lifetimes and their gates.")
    parser.add_argument("input", help="Parsek.dll, an ikdasm .il dump or an ilfacts.py --json file")
    parser.add_argument("--tests", default=str(DEFAULT_TESTS_DLL),
                        help="Parsek.Tests.dll (testWrittenUntagged; default: its Debug build path)")
    parser.add_argument("--write-pins", action="store_true", help="re-pin lifetime_pins.toml from this input")
    parser.add_argument("--pins", default=str(DEFAULT_PINS))
    parser.add_argument("--declaration", default=str(DEFAULT_DECLARATION))
    parser.add_argument("--json", help="write the report here")
    args = parser.parse_args(argv)
    for path in (args.input, args.tests):
        if args.write_pins and re.search(r"[\\/]Release[\\/]", str(path)):
            parser.error("pin from the Debug builds CI measures, not a Release build")
    decl = load_declaration(args.declaration)
    shape = declaration_problems(decl) + reset_gate_problems(decl, load_reset_gate())
    for problem in shape:
        print("DECLARATION " + problem)
    result, _facts = analyse_paths(args.input, args.tests, args.declaration)
    print("\n".join(format_report(result)))
    if args.write_pins and (shape or result["problems"]):
        parser.error("fix the declaration problems above before pinning")
    if args.json:
        Path(args.json).write_text(json.dumps(result, indent=1, sort_keys=True) + "\n", encoding="utf-8")
    pins_path = Path(args.pins)
    if args.write_pins:
        import datetime  # noqa: PLC0415

        pins_path.write_text(render_pins(result["gates"], Path(args.input).name, datetime.date.today().isoformat()),
                             encoding="utf-8", newline="\n")
        print("Wrote %s" % pins_path)
    elif pins_path.is_file():
        pins = load_pins(pins_path)
        problems = ratchet_problems(result["gates"], pins, result["details"])
        for line in problems or ["the lifetime gates hold"]:
            print("  " + line)
        for line in ratchet_slack(result["gates"], pins):
            print("  slack (re-pin to tighten): " + line)
        return 1 if problems or shape or result["problems"] else 0
    return 1 if shape or result["problems"] else 0


if __name__ == "__main__":
    sys.exit(main())
