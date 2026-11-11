#!/usr/bin/env python3
"""Analyses over the compiled-IL facts (ilfacts.py), and the ratchets on them.

Five reports, each a pure function of the facts model (and, for test reach and
the test-only split, the test assembly's facts):

- static state: per module and owning type, reassignable statics, static
  readonly collections whose contents some method other than a static
  constructor changes, statics written (or mutated) from another production
  outer type, and statics whose address another production type takes (a
  read of a value-type member, or a `ref` / `out` write: reported, not gated);
- complexity: decisions + 1 per method with the compiler-generated code folded
  in, ranked, and counted at 20 / 50 / 100;
- game coupling: a method can reach the game when it names a live type or
  member of the game assemblies, or calls a method that can (interface and
  virtual calls reach every implementation and override in the assembly);
  per module, the share of instructions in such methods;
- uncalled code: production methods the roots do not reach (a fixpoint over
  calls and the use-reading rules, see `uncalled`), split by test reach when
  the test facts are given, with each root rule's weight (how many live
  methods it alone kept: no live caller);
- test reach: per module, the share of production methods (and instructions)
  a unit test calls directly, and the complex methods none does; per method
  how many test methods call it directly, and the "where a test is worth
  most" ranking (`risk_table`: decisions, weighted up when no test calls the
  method directly and by its type's busiest file's commits in the history
  window, with the type's line coverage from the snapshot as a column).

With a coverage snapshot (coverage.py, a Windows coverlet run committed as
scripts/arch/coverage_snapshot.json) the report adds `coverage`: lines covered
per production module and type, and how stale the snapshot is.

facts.toml (beside modules.toml) holds every list the rules read: the inert
game types, the root attributes and names, the mutating members.

The ratchets bind call sets, static write sets and per-method decisions,
never instruction counts or shares. Debug and Release builds of one compiler
agree on them; two compilers (CI's SDK and a local one) can move a method's
decisions by one or two, which the complexity watch band absorbs. They are
pinned in facts_pins.toml from a Debug build:

    python3 scripts/arch/ilanalysis.py Source/Parsek/bin/Debug/Parsek.dll                 # report
    python3 scripts/arch/ilanalysis.py Parsek.dll --tests Parsek.Tests.dll --json r.json   # with test reach
                                                       # (reads git history; --no-history skips it)
    python3 scripts/arch/ilanalysis.py Source/Parsek/bin/Debug/Parsek.dll --write-pins    # re-pin

An input is an assembly, an ikdasm `.il` dump or a facts `.json` from
`ilfacts.py --json`. Everything except `main` and the loaders is pure.
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

sys.path.insert(0, str(Path(__file__).resolve().parent))

import archview  # noqa: E402
import ilfacts  # noqa: E402
import ilgraph  # noqa: E402

HERE = Path(__file__).resolve().parent
DEFAULT_CONFIG = HERE / "facts.toml"
DEFAULT_PINS = HERE / "facts_pins.toml"
THRESHOLDS = (20, 50, 100)
COMPLEXITY_GATE = 50
# Every method at COMPLEXITY_GATE - COMPLEXITY_WATCH_BAND or more is pinned, so
# a compiler that counts a method one or two decisions higher than the one
# that pinned it cannot red; a method that grows from below the band still
# does. Leaving the band needs no re-pin.
COMPLEXITY_WATCH_BAND = 5
UNTESTED_DECISIONS = 30
REPIN_COMMAND = (
    "python3 scripts/arch/ilanalysis.py Source/Parsek/bin/Debug/Parsek.dll --write-pins"
    " (after a Debug build of this tree; never from a Release build)"
)
ACCESSOR_PREFIXES = ("get_", "set_", "add_", "remove_")
# The order the root rules are tried in; a method kept by several counts
# under the first.
ROOT_RULES = (
    "static constructor",
    "delegate type",
    "attribute",
    "harmony patch method",
    "unity message",
    "object member or operator",
    "overrides an outside method",
    "implements an outside interface",
    "dispatch from a used slot",
    "slot with a used implementation",
    "accessor of a used property",
    "constructor of a used type",
    "named in a string of its own type",
    "named in a string elsewhere",
    "compiler-generated",
)


def load_config(path=DEFAULT_CONFIG):
    with open(path, "rb") as handle:
        return tomllib.load(handle)


def _matches(text, patterns):
    return any(fnmatch.fnmatchcase(text, pattern) for pattern in patterns)


def _key_type(key):
    return key.split("::", 1)[0]


def _key_name(key):
    rest = key.split("::", 1)[1]
    return rest.split("(", 1)[0].split("``", 1)[0]


def _tail(key, name):
    """What follows the method name in its key: ``N, the parameters, a conversion's return."""
    return key.split("::", 1)[1][len(name):]


def _param_count(tail):
    inside = tail[tail.find("(") + 1: tail.rfind(")")]
    return len(ilgraph._split_top_level(inside, ",")) if inside.strip() else 0


def _same_slot(tail_a, tail_b):
    if tail_a == tail_b:
        return True
    # A generic base or interface prints its parameters positionally (`!0`),
    # its closed implementation concretely: match by shape then.
    if "!" in tail_a or "!" in tail_b:
        arity_a = tail_a.split("(", 1)[0]
        arity_b = tail_b.split("(", 1)[0]
        return arity_a == arity_b and _param_count(tail_a) == _param_count(tail_b)
    return False


def _strip_generics(text):
    out = []
    depth = 0
    for char in text:
        if char == "<":
            depth += 1
        elif char == ">":
            depth -= 1
        elif depth == 0:
            out.append(char)
    return "".join(out)


def explicit_parts(name):
    """(interface text, member) for an explicit implementation name, else None."""
    if not name or name.startswith((".", "<")) or "." not in name:
        return None
    plain = _strip_generics(name)
    if "." not in plain:
        return None
    interface, member = plain.rsplit(".", 1)
    return interface, member


def complexity(entry):
    return entry["decisions"] + entry.get("generated", {}).get("decisions", 0) + 1


def size(entry):
    return entry["instructions"] + entry.get("generated", {}).get("instructions", 0)


class Index:
    """Lookups over one facts model: production scope, callers, the type
    hierarchy and the virtual slots each method fills."""

    def __init__(self, facts, tooling=()):
        self.facts = facts
        self.types = facts["types"]
        self.methods = facts["methods"]
        self.fields = facts["fields"]
        self.tooling = set(tooling)
        self.by_type = defaultdict(list)
        for key, entry in self.methods.items():
            self.by_type[entry["type"]].append(key)
        self.callers = defaultdict(set)
        for key, entry in self.methods.items():
            for callee in entry["calls"]:
                self.callers[callee].add(key)
        for path, entry in self.types.items():
            for callee in entry["unattributed"].get("calls", ()):
                self.callers[callee].add(path + "::" + ilfacts.UNATTRIBUTED)
        self.dotted_interfaces = {
            path.replace("/", "."): path for path, entry in self.types.items() if entry["kind"] == "interface"
        }
        self._ancestors = {}
        self._interfaces = {}
        self.slots = {}
        self.outside_override = set()
        self.outside_interface = set()
        self.implementers = defaultdict(set)
        for key in self.methods:
            self._resolve_slots(key)

    # --- scope ---------------------------------------------------------------

    def module(self, type_path):
        entry = self.types.get(type_path)
        return entry["module"] if entry else None

    def production(self, type_path):
        module = self.module(type_path)
        return module is not None and module not in self.tooling

    # --- hierarchy -----------------------------------------------------------

    def ancestors(self, path):
        """[path, base, base's base, ...] while the bases are in this assembly."""
        if path not in self._ancestors:
            chain = [path]
            seen = {path}
            base = self.types[path]["base"] if path in self.types else None
            while base and base in self.types and base not in seen:
                chain.append(base)
                seen.add(base)
                base = self.types[base]["base"]
            self._ancestors[path] = chain
        return self._ancestors[path]

    def outside_base(self, path):
        """The first base outside this assembly (`asm|Type`), or None."""
        last = self.ancestors(path)[-1]
        base = self.types[last]["base"] if last in self.types else None
        return base if base and "|" in base else None

    def engine_base(self, path):
        """True when an outside, non-BCL base class (MonoBehaviour, PartModule ...) is in the chain."""
        base = self.outside_base(path)
        return bool(base) and not ilgraph.BCL_ASSEMBLY_RE.match(base.split("|", 1)[0])

    def is_attribute_type(self, path):
        return self.outside_base(path) == "mscorlib|System.Attribute"

    def interfaces(self, path):
        """(in-assembly interfaces, outside interfaces) of a type, its bases and parent interfaces."""
        if path not in self._interfaces:
            inside, outside = set(), set()
            pending = []
            for ancestor in self.ancestors(path):
                pending.extend(self.types[ancestor].get("interfaces", ()))
            while pending:
                name = pending.pop()
                if name in self.types:
                    if name not in inside:
                        inside.add(name)
                        pending.extend(self.types[name].get("interfaces", ()))
                else:
                    outside.add(name)
            self._interfaces[path] = (inside, outside)
        return self._interfaces[path]

    def _resolve_slots(self, key):
        entry = self.methods[key]
        flags = entry["flags"]
        found = set()
        if "virtual" not in flags or entry["type"] not in self.types:
            self.slots[key] = found
            return
        path = entry["type"]
        name = entry["name"]
        tail = _tail(key, name)
        for ancestor in self.ancestors(path)[1:]:
            for other in self.by_type.get(ancestor, ()):
                candidate = self.methods[other]
                if candidate["name"] == name and "virtual" in candidate["flags"] and _same_slot(
                        _tail(other, name), tail):
                    found.add(other)
        overrides_inside = bool(found)
        explicit = explicit_parts(name)
        if explicit is not None:
            interface = self.dotted_interfaces.get(explicit[0])
            if interface is None:
                self.outside_interface.add(key)
            else:
                for other in self.by_type.get(interface, ()):
                    if self.methods[other]["name"] == explicit[1] and _same_slot(
                            _tail(other, explicit[1]), tail):
                        found.add(other)
        elif self.types[path]["kind"] != "interface":
            inside, outside = self.interfaces(path)
            for interface in inside:
                for other in self.by_type.get(interface, ()):
                    if self.methods[other]["name"] == name and _same_slot(_tail(other, name), tail):
                        found.add(other)
            if "newslot" not in flags and not overrides_inside:
                self.outside_override.add(key)
            elif "newslot" in flags and not found and outside:
                self.outside_interface.add(key)
        self.slots[key] = found
        for slot in found:
            self.implementers[slot].add(key)

    def accessor_siblings(self, key):
        """The other accessors of the property or event an accessor belongs to."""
        entry = self.methods[key]
        name = entry["name"]
        explicit = explicit_parts(name)
        prefix_name = explicit[1] if explicit else name
        for prefix in ACCESSOR_PREFIXES:
            if prefix_name.startswith(prefix):
                member = prefix_name[len(prefix):]
                head = name[: len(name) - len(prefix_name)]
                wanted = {head + p + member for p in ACCESSOR_PREFIXES}
                return [k for k in self.by_type.get(entry["type"], ())
                        if k != key and self.methods[k]["name"] in wanted]
        return None


# --- a. static state -----------------------------------------------------------


def static_state(index, config):
    mutating = set(config["statics"]["mutating_members"])
    rows = {}
    setter_callers = {}
    for path in index.types:
        for key in index.by_type.get(path, ()):
            name = index.methods[key]["name"]
            if name.startswith("set_") and "static" in index.methods[key]["flags"]:
                setter_callers[(path, name[4:])] = index.callers.get(key, set())

    def other_production(field_key, writers):
        own = ilfacts.outermost(_key_type(field_key))
        return sorted(
            w for w in writers
            if ilfacts.outermost(_key_type(w)) != own and index.production(_key_type(w))
        )

    for key, field in index.fields.items():
        path = _key_type(key)
        name = key.split("::", 1)[1]
        if not field["static"] or field["literal"] or not index.production(path):
            continue
        if name.startswith("<") and not field["backingFor"]:
            continue
        collection = bool(ilfacts.COLLECTION_TYPE_RE.search(field["fieldType"]))
        if field["initonly"] and not collection:
            continue
        mutators = sorted({
            call.split("|", 1)[0] for call in field.get("receiverCalls", ())
            if call.rsplit("|", 1)[1] in mutating
            and not _key_name(call.split("|", 1)[0]).startswith(".cctor")
        })
        # An `ldsflda` is how C# reads a member of a value-type static or calls
        # a method on it (`Ledger.StateVersion.ToString(ic)`), so it is no
        # write; it is reported on its own (a `ref` / `out` write is one too).
        writers = set(field.get("writers", ()))
        if field["backingFor"]:
            writers |= setter_callers.get((path, field["backingFor"]), set())
        row = {
            "type": path,
            "module": index.module(path),
            "reassignable": not field["initonly"],
            "collection": collection,
            "mutated": bool(field["initonly"] and mutators),
            "writtenElsewhereBy": other_production(key, writers) if not field["initonly"] else [],
            "mutatedElsewhereBy": other_production(key, mutators) if field["initonly"] else [],
            "addressTakenElsewhereBy": (
                other_production(key, field.get("addressTakenBy", ())) if not field["initonly"] else []),
        }
        rows[key] = row

    def tally(selector):
        out = defaultdict(lambda: Counter())
        for key, row in rows.items():
            bucket = out[selector(key, row)]
            bucket["reassignable"] += row["reassignable"]
            bucket["mutatedCollections"] += row["mutated"]
            bucket["readonlyCollections"] += bool(row["collection"] and not row["reassignable"])
            bucket["writtenElsewhere"] += bool(row["writtenElsewhereBy"])
            bucket["mutatedElsewhere"] += bool(row["mutatedElsewhereBy"])
            bucket["addressTakenElsewhere"] += bool(row["addressTakenElsewhereBy"])
        return {name: dict(counts) for name, counts in out.items()}

    by_module = tally(lambda _k, row: row["module"])
    by_type = tally(lambda _k, row: ilfacts.outermost(row["type"]))
    cross = {key: row["writtenElsewhereBy"] for key, row in rows.items() if row["writtenElsewhereBy"]}
    return {
        "fields": rows,
        "byModule": by_module,
        "byType": by_type,
        "totals": dict(sum((Counter(v) for v in by_module.values()), Counter())),
        "crossTypeWrites": dict(sorted(cross.items())),
    }


# --- b. complexity ---------------------------------------------------------------


def complexity_report(index):
    rows = []
    for key, entry in index.methods.items():
        if not index.production(entry["type"]):
            continue
        rows.append({
            "key": key,
            "module": index.module(entry["type"]),
            "complexity": complexity(entry),
            "ownComplexity": entry["decisions"] + 1,
            "instructions": size(entry),
        })
    rows.sort(key=lambda r: (-r["complexity"], r["key"]))
    counts = {t: sum(1 for r in rows if r["complexity"] >= t) for t in THRESHOLDS}
    return {"methods": rows, "atLeast": counts}


def complexity_gate_names(report, threshold=COMPLEXITY_GATE):
    """`Type::Name` of every production method at or above `threshold` (overloads merge)."""
    return sorted({_key_type(r["key"]) + "::" + _key_name(r["key"])
                   for r in report["methods"] if r["complexity"] >= threshold})


# --- c. game coupling ------------------------------------------------------------


def _live_type(text, coupling):
    assembly, _sep, path = text.partition("|")
    return _matches(assembly, coupling["game_assemblies"]) and not _matches(path, coupling["inert_types"])


def _live_member(text, coupling):
    assembly, _sep, member = text.partition("|")
    if not _matches(assembly, coupling["game_assemblies"]):
        return False
    path = member.split("::", 1)[0]
    return not _matches(path, coupling["inert_types"]) or _matches(member, coupling["live_members"])


def direct_game_refs(entry, coupling, memo=None):
    """The live game types and members a method's body or signature names. `memo`, a dict
    the caller keeps for one pass over one `coupling`, caches the answer per name (the
    pattern match is the cost; the same names recur in thousands of methods)."""
    if memo is None:
        memo = {}

    def live(kind, text):
        key = (kind, text)
        if key not in memo:
            memo[key] = _live_type(text, coupling) if kind == "t" else _live_member(text, coupling)
        return memo[key]

    found = set()
    for scope in ("body", "signature"):
        found.update(t for t in entry["external"][scope] if live("t", t))
    found.update(m for m in entry.get("externalMembers", ()) if live("m", m))
    return found


def game_reach(index, config, via=None):
    """(direct, reach): the methods that name a live game type or member themselves, and
    every method that can reach the game. `via`, when given, is filled with
    {method: the callee or implementation it was reached through} (a path for messages)."""
    coupling = config["coupling"]
    not_followed = tuple(coupling.get("not_followed", ()))
    memo = {}
    direct = {key for key, entry in index.methods.items() if direct_game_refs(entry, coupling, memo)}
    reach = set(direct)
    # Sorted, so `via` (the one path per method messages print) does not depend
    # on set iteration order; the sets themselves do not depend on it.
    pending = sorted(direct, reverse=True)

    def followed(callee):
        path = _key_type(callee)
        return not any(path == t or path.startswith(t + "/") for t in not_followed)

    while pending:
        key = pending.pop()
        if not followed(key):
            continue
        # Who calls it, and who calls a slot it fills (virtual dispatch).
        callers = set(index.callers.get(key, ()))
        for slot in index.slots.get(key, ()):
            if followed(slot):
                callers |= index.callers.get(slot, set())
        for caller in sorted(callers):
            if caller in index.methods and caller not in reach:
                reach.add(caller)
                pending.append(caller)
                if via is not None:
                    via[caller] = key
    return direct, reach


def coupling_report(index, config):
    via = {}
    direct, reach = game_reach(index, config, via)
    rows = defaultdict(Counter)
    for key, entry in index.methods.items():
        if not index.production(entry["type"]):
            continue
        row = rows[index.module(entry["type"])]
        n = size(entry)
        row["methods"] += 1
        row["instructions"] += n
        if key in direct:
            row["directInstructions"] += n
        if key in reach:
            row["reachInstructions"] += n
            row["reachMethods"] += 1
    out = {}
    for module, row in rows.items():
        total = row["instructions"] or 1
        out[module] = dict(row, share=row["reachInstructions"] / total, directShare=row["directInstructions"] / total)
    total = sum(r["instructions"] for r in rows.values()) or 1
    return {
        "byModule": dict(sorted(out.items(), key=lambda kv: -kv[1]["instructions"])),
        "share": sum(r["reachInstructions"] for r in rows.values()) / total,
        "reach": reach,
        "direct": direct,
        "via": via,
    }


# --- d. uncalled code ------------------------------------------------------------


def _attribute_root(entry, ignored):
    for attribute in entry["attributes"]:
        if not _matches(attribute, ignored):
            return attribute.rsplit("|", 1)[-1].rsplit(".", 1)[-1]
    return None


def _type_attributes(index, path, ignored):
    """Non-ignored attributes on a type or on any type enclosing it."""
    found = []
    parts = path.split("/")
    for size_ in range(len(parts), 0, -1):
        entry = index.types.get("/".join(parts[:size_]))
        if entry:
            found.extend(a for a in entry["attributes"] if not _matches(a, ignored))
    return found


def _string_names(entry):
    """The names a string literal can match a method by: its own, and its property's."""
    name = (explicit_parts(entry["name"]) or (None, entry["name"]))[1]
    member = name
    for prefix in ACCESSOR_PREFIXES:
        if name.startswith(prefix):
            member = name[len(prefix):]
    return {name, member}


def uncalled(index, config, test_calls=None):
    """Production methods no root reaches: the dead code of the assembly.

    Live is a fixpoint. It starts from every non-production method (tooling
    counts as a caller), the generated code no single method owns, and every
    production method a root rule keeps on its own (static constructor,
    delegate type, attribute, Harmony patch method, Unity message, object
    member or operator, an outside override or interface implementation, a
    constructor of a type another type names or the engine creates,
    compiler-generated). It grows by calls from live methods, and by the
    rules that read use: an implementation of an invoked slot, an abstract
    or interface slot of a live implementation, the sibling accessor of a
    live accessor, the constructor of a type with a live member, a method
    named by a string literal in a live method. Every step reads only live
    methods, so removing a dead method can never make another one dead.

    Returns {"rootedBy": {rule: [keys]}, "uncalled": [dead keys], and, when
    `test_calls` (production keys a test calls, dispatch already expanded) is
    given, "noCaller" and "testOnly"}. A live method is counted under the
    first rule that keeps it only when no other live method calls it, so each
    rule's list is what that rule alone keeps.
    """
    rules = config["uncalled"]
    ignored = rules["ignored_attributes"]
    patch_methods = set(rules["patch_methods"])
    unity = set(rules["unity_messages"])
    object_members = set(rules["object_members"])
    hooks = set(rules["patch_hooks"])

    def is_patch_type(path):
        # A [HarmonyPatch] type, or one handed to Harmony by `typeof` (named
        # by another type) that declares a static patch hook.
        if any(a in rules["patch_type_attributes"] for a in _type_attributes(index, path, ignored)):
            return True
        return bool(index.types[path]["namedByOthers"]) and any(
            index.methods[k]["name"] in hooks and "static" in index.methods[k]["flags"]
            for k in index.by_type.get(path, ()))

    methods = index.methods
    production = {key for key, entry in methods.items() if index.production(entry["type"])}

    def first_rules(key):
        entry = methods[key]
        path = entry["type"]
        name = entry["name"]
        bare = (explicit_parts(name) or (None, name))[1]
        if name == ".cctor":
            return "static constructor"
        if index.types[path]["kind"] == "delegate":
            return "delegate type"
        if _attribute_root(entry, ignored):
            return "attribute"
        if bare in patch_methods and is_patch_type(path):
            return "harmony patch method"
        if name in unity and index.engine_base(path):
            return "unity message"
        if bare in object_members or bare.startswith("op_"):
            return "object member or operator"
        if key in index.outside_override:
            return "overrides an outside method"
        if key in index.outside_interface:
            return "implements an outside interface"
        return None

    def created_outside(path):
        # A type another type names, the engine creates, or that is an attribute.
        return bool(index.types[path]["namedByOthers"] or index.engine_base(path)
                    or index.is_attribute_type(path) or _type_attributes(index, path, ignored))

    def slot_like(key):
        return "abstract" in methods[key]["flags"] or index.types[methods[key]["type"]]["kind"] == "interface"

    by_string_name = defaultdict(set)
    for key in production:
        for name in _string_names(methods[key]):
            by_string_name[name].add(key)

    live, invoked, pending = set(), set(), []

    def mark(key, calls_it):
        if key not in methods:
            return
        if calls_it and key not in invoked:
            invoked.add(key)
            pending.append(key)
        if key not in live:
            live.add(key)
            pending.append(key)

    first = {}
    for key, entry in methods.items():
        if key not in production:
            mark(key, False)
            continue
        rule = first_rules(key)
        if rule:
            first[key] = rule
            mark(key, True)
        elif entry["compilerGenerated"] or (entry["name"] == ".ctor" and created_outside(entry["type"])):
            mark(key, False)
    for entry in index.types.values():
        for callee in entry["unattributed"].get("calls", ()):
            mark(callee, True)
    while pending:
        key = pending.pop()
        entry = methods[key]
        for callee in entry["calls"]:
            mark(callee, True)
        if key in invoked:
            for impl in index.implementers.get(key, ()):
                mark(impl, True)
        for slot in index.slots.get(key, ()):
            if slot_like(slot):
                mark(slot, False)
        for sibling in index.accessor_siblings(key) or ():
            mark(sibling, False)
        if entry["name"] != ".ctor":
            for other in index.by_type.get(entry["type"], ()):
                if methods[other]["name"] == ".ctor":
                    mark(other, False)
        for text in entry.get("strings", ()):
            for named in by_string_name.get(text, ()):
                mark(named, False)

    strings_by_outer = defaultdict(set)
    every_string = set()
    for key in live:
        strings = methods[key].get("strings", ())
        strings_by_outer[ilfacts.outermost(methods[key]["type"])].update(strings)
        every_string.update(strings)
    unattributed_suffix = "::" + ilfacts.UNATTRIBUTED

    def kept_by(key):
        entry = methods[key]
        path = entry["type"]
        if key in first:
            return first[key]
        if any(slot in invoked for slot in index.slots.get(key, ())):
            return "dispatch from a used slot"
        if slot_like(key) and any(impl in live for impl in index.implementers.get(key, ())):
            return "slot with a used implementation"
        if any(s in live for s in index.accessor_siblings(key) or ()):
            return "accessor of a used property"
        if entry["name"] == ".ctor" and (created_outside(path) or any(
                k in live for k in index.by_type.get(path, ()) if methods[k]["name"] != ".ctor")):
            return "constructor of a used type"
        names = _string_names(entry)
        if names & strings_by_outer[ilfacts.outermost(path)]:
            return "named in a string of its own type"
        if names & every_string:
            return "named in a string elsewhere"
        if entry["compilerGenerated"]:
            return "compiler-generated"
        return None

    by_rule = {rule: [] for rule in ROOT_RULES}
    for key in production & live:
        if any(c != key and (c in live or c.endswith(unattributed_suffix)) for c in index.callers.get(key, ())):
            continue
        rule = kept_by(key)
        if rule:
            by_rule[rule].append(key)
    left = sorted(production - live)
    out = {"rootedBy": {rule: sorted(keys) for rule, keys in by_rule.items()}, "uncalled": left}
    if test_calls is not None:
        out["testOnly"] = [key for key in left if key in test_calls]
        out["noCaller"] = [key for key in left if key not in test_calls]
    return out


def is_test_hook(key, suffixes):
    name = _key_name(key)
    return next((suffix for suffix in suffixes if name.endswith(suffix)), None)


def uncalled_gate_keys(report, config):
    """The ratchet's set: production methods no root reaches, test hooks by name left out.

    Read from Parsek.dll alone, so it holds methods only a test calls too
    (that is production code the game never runs; a hook named as one does
    not count).
    """
    suffixes = config["uncalled"]["test_hook_suffixes"]
    return sorted(key for key in report["uncalled"] if not is_test_hook(key, suffixes))


# --- e. test reach ---------------------------------------------------------------


def test_callers(index, tests_facts):
    """({key: test methods calling it directly}, unmatched) over every key of the assembly.

    A test method's call on a slot (an interface or virtual member) credits the
    slot and each of its implementations and overrides (`Index.implementers`),
    once per test method however often it calls."""
    prefix = (index.facts.get("assembly") or ilgraph.ROOT_NAMESPACE) + "|"
    callers = defaultdict(set)
    unmatched = set()
    for test_key, entry in tests_facts["methods"].items():
        for call in entry.get("crossAssemblyCalls", ()):
            if not call.startswith(prefix):
                continue
            key = call[len(prefix):]
            if key in index.methods:
                callers[key].add(test_key)
                for implementation in index.implementers.get(key, ()):
                    callers[implementation].add(test_key)
            else:
                unmatched.add(key)
    return dict(callers), unmatched


def test_called(index, tests_facts):
    """Production keys a test method calls directly (calls on a slot reach its implementations)."""
    callers, unmatched = test_callers(index, tests_facts)
    return set(callers), unmatched


def test_reach_report(index, called, callers=None):
    """Per module, the share a test calls directly; the complex methods none does.

    With `callers` (test_callers' map) it also counts, per production method a
    test calls, how many distinct test methods do (`testCallers`), and the
    production types at least one of whose methods a test calls (`types`)."""
    rows = defaultdict(Counter)
    untested = []
    counts = {}
    all_types = set()
    for key, entry in index.methods.items():
        if not index.production(entry["type"]):
            continue
        all_types.add(entry["type"])
        row = rows[index.module(entry["type"])]
        n = size(entry)
        row["methods"] += 1
        row["instructions"] += n
        if key in called:
            row["reachedMethods"] += 1
            row["reachedInstructions"] += n
            if callers is not None:
                counts[key] = len(callers.get(key, ()))
        elif complexity(entry) - 1 >= UNTESTED_DECISIONS:
            untested.append({"key": key, "module": index.module(entry["type"]),
                             "decisions": complexity(entry) - 1, "instructions": n})
    untested.sort(key=lambda r: (-r["decisions"], r["key"]))
    out = {}
    for module, row in rows.items():
        out[module] = dict(row, methodShare=row["reachedMethods"] / (row["methods"] or 1),
                           instructionShare=row["reachedInstructions"] / (row["instructions"] or 1))
    result = {"byModule": out, "untestedComplex": untested}
    if callers is not None:
        result["testCallers"] = counts
        result["types"] = {"production": len(all_types),
                           "reached": len({_key_type(key) for key in counts})}
    return result


# --- where a test is worth most ------------------------------------------------------

# The risk score of a method: its decisions (generated code folded in), times
# RISK_UNTESTED_WEIGHT when no test method calls it directly, times 1 plus the
# commits of its type's busiest source file in the history window over
# RISK_CHURN_CAP (capped at 1, so a file changed RISK_CHURN_CAP times or more
# doubles it). Every factor is monotone: more decisions or more commits never
# lower the score, a direct test caller never raises it.
RISK_TOP = 25
RISK_UNTESTED_WEIGHT = 2
RISK_CHURN_CAP = 20


def risk_score(decisions, test_callers, commits):
    """decisions x (2 untested | 1) x (1 + min(commits, 20) / 20); unknown commits count as 0."""
    untested = RISK_UNTESTED_WEIGHT if not test_callers else 1
    churn = 1.0 + min(commits or 0, RISK_CHURN_CAP) / float(RISK_CHURN_CAP)
    return decisions * untested * churn


def file_commit_counts(commits):
    """{source-relative file: commits touching it} from archview.parse_history's commits."""
    counts = Counter()
    for commit in commits:
        counts.update(set(commit["files"]))
    return dict(counts)


def type_commits(type_paths, type_files, file_commits):
    """{type path: commits of its busiest source file}; a type no file declares is left out.

    A partial type's methods sit in one of its files, which the IL does not
    say, so the type takes its busiest file's count (the max, not the sum: one
    commit editing two parts is one change)."""
    out = {}
    for path in type_paths:
        files = ilfacts.files_for(path, type_files)
        if files:
            out[path] = max(file_commits.get(rel, 0) for rel in files)
    return out


def risk_table(result, churn=None, top=RISK_TOP):
    """The ranked "where a test is worth most" rows from a report with test reach.

    `churn` is {since, commits, typeCommits} (history_churn), None when the
    history was not read: the commits column is then blank and counts as 0.
    The type coverage comes from the report's `coverage` (None without a
    snapshot). Rows: key, module, type, decisions, testCallers, typeReached,
    fileCommits, typeLineShare, score; sorted by score, then decisions, then key."""
    reach = result.get("testReach")
    if not reach or "testCallers" not in reach:
        return None
    callers = reach["testCallers"]
    reached_types = {_key_type(key) for key in callers}
    commits_of = (churn or {}).get("typeCommits")
    cover = (result.get("coverage") or {}).get("byType")
    rows = []
    for row in result["complexity"]["methods"]:
        decisions = row["complexity"] - 1
        if decisions <= 0:
            continue
        key = row["key"]
        path = _key_type(key)
        n_callers = callers.get(key, 0)
        commits = commits_of.get(path) if commits_of is not None else None
        rows.append({
            "key": key,
            "module": row["module"],
            "type": path,
            "decisions": decisions,
            "testCallers": n_callers,
            "typeReached": path in reached_types,
            "fileCommits": commits,
            "typeLineShare": cover.get(path) if cover is not None else None,
            "score": round(risk_score(decisions, n_callers, commits), 2),
        })
    rows.sort(key=lambda r: (-r["score"], -r["decisions"], r["key"]))
    return {
        "rows": rows[:top],
        "candidates": len(rows),
        "since": (churn or {}).get("since"),
        "historyCommits": (churn or {}).get("commits"),
        "coverage": cover is not None,
    }


# --- coverage (a committed snapshot of a Windows coverlet run) ------------------------


def coverage_report(index, snapshot):
    """The snapshot (coverage.py) read against this assembly: per production
    module the lines covered / valid, the line share per production type, and
    how stale the snapshot is. None without a snapshot.

    Staleness counts types with code (a method with a body) in this assembly
    the snapshot does not list, and snapshot types this assembly lacks; it
    is reported, never failed."""
    if snapshot is None:
        return None
    types = snapshot["types"]
    with_code = {entry["type"] for entry in index.methods.values()
                 if entry["instructions"] > 0 and entry["type"] in index.types}
    by_module = defaultdict(Counter)
    branch_unknown = defaultdict(int)
    by_type = {}
    for path, row in types.items():
        if path not in index.types or not index.production(path):
            continue
        lines = row["lines"]
        by_type[path] = lines["covered"] / lines["valid"] if lines["valid"] else None
        counter = by_module[index.module(path)]
        counter["linesValid"] += lines["valid"]
        counter["linesCovered"] += lines["covered"]
        if row.get("branches") is None:
            branch_unknown[index.module(path)] += 1
        else:
            counter["branchesValid"] += row["branches"]["valid"]
            counter["branchesCovered"] += row["branches"]["covered"]
    modules = {}
    for module in sorted(by_module):
        c = by_module[module]
        modules[module] = {
            "linesValid": c["linesValid"], "linesCovered": c["linesCovered"],
            "lineShare": c["linesCovered"] / c["linesValid"] if c["linesValid"] else None,
            "branchesValid": c["branchesValid"], "branchesCovered": c["branchesCovered"],
            "branchShare": c["branchesCovered"] / c["branchesValid"] if c["branchesValid"] else None,
            "branchesUnknownTypes": branch_unknown[module],
        }
    valid = sum(m["linesValid"] for m in modules.values())
    covered = sum(m["linesCovered"] for m in modules.values())
    not_in_snapshot = sorted(with_code - set(types))
    not_in_assembly = sorted(set(types) - set(index.types))
    return {
        "commit": snapshot["commit"],
        "date": snapshot["date"],
        "configuration": snapshot.get("configuration"),
        "linesValid": valid,
        "linesCovered": covered,
        "lineShare": covered / valid if valid else None,
        "byModule": modules,
        "byType": by_type,
        "stale": {"notInSnapshot": len(not_in_snapshot), "notInAssembly": len(not_in_assembly),
                  "notInSnapshotExamples": not_in_snapshot[:5], "notInAssemblyExamples": not_in_assembly[:5]},
    }


# --- the whole report -------------------------------------------------------------


def report(facts, tooling=(), config=None, tests_facts=None, headless_modules=None, coverage=None, churn=None):
    """Every analysis over one facts model, JSON-ready (sets become sorted lists).

    `headless_modules` (modules.toml [headless.modules]) adds `headless`, the
    leak report of headless.py over the same game reach; None leaves it None.
    `coverage` (a coverage.py snapshot) adds `coverage`; None leaves it None.
    `churn` (history_churn) feeds the risk table's commits column, which test
    reach carries as `testReach.risk`."""
    config = config or load_config()
    index = Index(facts, tooling)
    test_calls = None
    callers = None
    unmatched = []
    if tests_facts is not None:
        callers, unmatched_set = test_callers(index, tests_facts)
        test_calls = set(callers)
        unmatched = sorted(unmatched_set)
    statics = static_state(index, config)
    cx = complexity_report(index)
    coupling = coupling_report(index, config)
    dead = uncalled(index, config, test_calls)
    suffixes = config["uncalled"]["test_hook_suffixes"]
    sizes = {key: size(index.methods[key]) for key in dead["uncalled"]}
    out = {
        "assembly": facts.get("assembly"),
        "statics": statics,
        "complexity": cx,
        "coupling": {"byModule": coupling["byModule"], "share": coupling["share"]},
        "uncalled": dict(dead, instructions=sizes,
                         modules={key: index.module(index.methods[key]["type"]) for key in dead["uncalled"]}),
        "testReach": None,
        "headless": None,
        "coverage": coverage_report(index, coverage),
        "gates": {
            "crossTypeStaticWrites": sorted(statics["crossTypeWrites"]),
            "complexity50": complexity_gate_names(cx),
            "complexityWatch": complexity_gate_names(cx, COMPLEXITY_GATE - COMPLEXITY_WATCH_BAND),
            "uncalledInAssembly": uncalled_gate_keys(dead, config),
        },
    }
    if headless_modules is not None:
        import headless  # noqa: PLC0415 (it imports this module)

        out["headless"] = headless.analyse(index, config, headless_modules, coupling["direct"], coupling["reach"],
                                           coupling["via"])
    if test_calls is not None:
        hooks = Counter()
        for key in dead["testOnly"]:
            hooks[is_test_hook(key, suffixes) or "other"] += 1
        out["uncalled"]["testOnlyBySuffix"] = dict(hooks)
        out["testReach"] = dict(test_reach_report(index, test_calls, callers), unmatchedTestCalls=unmatched)
        out["testReach"]["risk"] = risk_table(out, churn)
    return out


# --- ratchets ---------------------------------------------------------------------


def load_pins(path=DEFAULT_PINS):
    with open(path, "rb") as handle:
        return tomllib.load(handle)


def render_pins(gates, source="", date=""):
    lines = [
        "# The ratchets over the compiled-IL facts (scripts/arch/ilanalysis.py), read by",
        "# scripts/arch/test_ilanalysis.py in CI's compiled-IL step. Machine-written:",
        "#   " + REPIN_COMMAND,
        "# crossTypeStaticWrites and uncalledInAssembly are counted: the measured set may",
        "# not outnumber `bound`. A swap inside the bound (one key out, another in) passes,",
        "# and slack after a fall is not enforced until a re-pin, so re-pin in the PR that",
        "# lowers a count; raise a bound only for a deliberate change the PR explains.",
        "# complexityWatch lists every method at complexity >= %d (the gate %d minus a"
        % (COMPLEXITY_GATE - COMPLEXITY_WATCH_BAND, COMPLEXITY_GATE),
        "# watch band of %d, so a compiler that counts a method a decision or two higher"
        % COMPLEXITY_WATCH_BAND,
        "# cannot red); a method not listed here reds when it reaches %d. A listed method"
        % COMPLEXITY_GATE,
        "# may grow without a red, and a rename or a move into another type needs a re-pin.",
    ]
    if source or date:
        lines.append("# Pinned from %s %s" % (source, date))
    for name in ("crossTypeStaticWrites", "uncalledInAssembly", "complexityWatch"):
        keys = gates[name]
        lines.append("")
        lines.append("[%s]" % name)
        if name != "complexityWatch":
            lines.append("bound = %d" % len(keys))
        lines.append("keys = [")
        lines.extend("    %s," % json.dumps(key) for key in keys)
        lines.append("]")
    return "\n".join(lines) + "\n"


def ratchet_problems(gates, pins, details=None, names=None):
    """Why the measured sets break the pins (empty when they hold).

    Counted sets (cross-type static writes, uncalled) red only when the count
    rises above the bound; the complexity gate reds when a method not in the
    pinned watch band reaches COMPLEXITY_GATE. `details` maps a key to a short explanation (its writers, its
    complexity) for the message; `names` limits the check to those ratchets.
    """
    details = details or {}
    problems = []

    def describe(keys):
        return "; ".join("%s%s" % (key, (" (" + details[key] + ")") if key in details else "") for key in keys)

    for name, label in (("crossTypeStaticWrites", "statics written by another production type"),
                        ("uncalledInAssembly", "production methods no root reaches in the assembly")):
        if names is not None and name not in names:
            continue
        measured = set(gates[name])
        pinned = set(pins[name]["keys"])
        bound = pins[name]["bound"]
        if len(measured) > bound:
            new = sorted(measured - pinned)
            gone = sorted(pinned - measured)
            problems.append(
                "%s: %d, pinned bound %d (+%d). New since the pin: %s.%s If this is deliberate, re-pin: %s"
                % (label, len(measured), bound, len(measured) - bound, describe(new) or "none",
                   (" Gone since the pin: %s." % "; ".join(gone)) if gone else "", REPIN_COMMAND)
            )
    new = sorted(set(gates["complexity50"]) - set(pins["complexityWatch"]["keys"]))
    if new and (names is None or "complexity50" in names):
        problems.append(
            "methods newly at complexity >= %d (decisions + 1, generated code folded in): %s."
            " Split the method, or if this is deliberate (a rename or a move counts), re-pin: %s"
            % (COMPLEXITY_GATE, describe(new), REPIN_COMMAND)
        )
    return problems


def ratchet_slack(gates, pins):
    """Bounds the measure is now below (a re-pin would tighten them)."""
    out = []
    for name in ("crossTypeStaticWrites", "uncalledInAssembly"):
        if len(gates[name]) < pins[name]["bound"]:
            out.append("%s: %d below the bound %d" % (name, len(gates[name]), pins[name]["bound"]))
    gone = set(pins["complexityWatch"]["keys"]) - set(gates["complexityWatch"])
    if gone:
        out.append("complexityWatch: %d pinned methods no longer in the watch band" % len(gone))
    return out


def gate_details(result):
    details = {}
    for key, writers in result["statics"]["crossTypeWrites"].items():
        details[key] = "written by " + ", ".join(writers[:3]) + (" ..." if len(writers) > 3 else "")
    for row in result["complexity"]["methods"]:
        name = _key_type(row["key"]) + "::" + _key_name(row["key"])
        if row["complexity"] >= COMPLEXITY_GATE and name not in details:
            details[name] = "complexity %d" % row["complexity"]
    for key, n in result["uncalled"]["instructions"].items():
        details[key] = "%d instructions" % n
    return details


# --- text summary -----------------------------------------------------------------


def _pct(value):
    return "%.0f%%" % (100.0 * value)


def _share_text(value):
    return "-" if value is None else _pct(value)


def risk_explanation(risk):
    """One sentence: the score and its window."""
    window = ("file commits since %s (%d commits, sweeps left out)" % (risk["since"], risk["historyCommits"])
              if risk.get("since") else "no history read, so commits count as 0")
    return ("decisions x %d when no test method calls the method directly x (1 + min(commits, %d) / %d),"
            " commits of its type's busiest source file; %s" % (RISK_UNTESTED_WEIGHT, RISK_CHURN_CAP,
                                                              RISK_CHURN_CAP, window))


def format_risk(risk, top=10):
    if not risk:
        return []
    lines = ["  where a test is worth most (top %d of %d methods with a decision; score = %s):"
             % (min(top, len(risk["rows"])), risk["candidates"], risk_explanation(risk)),
             "    %7s %5s %7s %7s %7s %6s  %s" % ("score", "dec", "callers", "commits", "type", "cover", "method")]
    for row in risk["rows"][:top]:
        lines.append("    %7.1f %5d %7d %7s %7s %6s  %s" % (
            row["score"], row["decisions"], row["testCallers"],
            "-" if row["fileCommits"] is None else row["fileCommits"],
            "reached" if row["typeReached"] else "no", _share_text(row["typeLineShare"]),
            row["key"].split("(", 1)[0]))
    return lines


def coverage_stale_line(cover):
    stale = cover["stale"]
    return ("Types with code in this assembly the snapshot does not list: %d (new since it, or code coverlet"
            " does not instrument); snapshot types this assembly lacks: %d (renamed or deleted since)"
            % (stale["notInSnapshot"], stale["notInAssembly"]))


def format_coverage(cover):
    lines = ["  line coverage (measured at %s, %s, %s): %s of %d production lines"
             % (cover["commit"], cover["date"], cover.get("configuration") or "?", _share_text(cover["lineShare"]),
                cover["linesValid"])]
    for module, row in sorted(cover["byModule"].items(), key=lambda kv: -kv[1]["linesValid"]):
        lines.append("    %-14s %6d / %6d  %5s  (branches %s)" % (
            module, row["linesCovered"], row["linesValid"], _share_text(row["lineShare"]),
            _share_text(row["branchShare"])))
    lines.append("    " + coverage_stale_line(cover))
    return lines


def format_report(result, top=15):
    lines = ["CODE FACTS (%s)" % result["assembly"]]
    totals = result["statics"]["totals"]
    lines.append(
        "  statics: %d reassignable, %d readonly collections (%d mutated outside a static constructor),"
        " %d reassignable written by another production type, %d collections mutated by one;"
        " address taken by another production type: %d (reads of a value-type member, or a ref / out write"
        " the gate does not see)"
        % (totals.get("reassignable", 0), totals.get("readonlyCollections", 0), totals.get("mutatedCollections", 0),
           totals.get("writtenElsewhere", 0), totals.get("mutatedElsewhere", 0),
           totals.get("addressTakenElsewhere", 0))
    )
    at = result["complexity"]["atLeast"]
    lines.append("  complexity (generated code folded in) >= 20 / 50 / 100: %d / %d / %d" % (at[20], at[50], at[100]))
    for row in result["complexity"]["methods"][:top]:
        lines.append("    %4d  %s" % (row["complexity"], row["key"]))
    lines.append("  can reach the game: %s of production instructions" % _pct(result["coupling"]["share"]))
    for module, row in result["coupling"]["byModule"].items():
        lines.append("    %-14s %8d instr  %4s  (direct %s)" % (
            module, row["instructions"], _pct(row["share"]), _pct(row["directShare"])))
    dead = result["uncalled"]
    lines.append("  not reached from the roots: %d methods, %d instructions" % (
        len(dead["uncalled"]), sum(dead["instructions"].values())))
    for rule in ROOT_RULES:
        if dead["rootedBy"].get(rule):
            lines.append("    kept by %-34s %d" % (rule + ":", len(dead["rootedBy"][rule])))
    if "noCaller" in dead:
        lines.append("    no unit test calls it either: %d; called from the test assembly: %d %s" % (
            len(dead["noCaller"]), len(dead["testOnly"]), dict(sorted(dead["testOnlyBySuffix"].items()))))
    if result["testReach"]:
        reach = result["testReach"]
        lines.append("  called directly by a unit test, per module (methods / instructions):")
        for module, row in sorted(reach["byModule"].items(), key=lambda kv: -kv[1]["instructions"]):
            lines.append("    %-14s %4s / %4s" % (module, _pct(row["methodShare"]), _pct(row["instructionShare"])))
        lines.append("  methods with >= %d decisions no unit test calls: %d" % (
            UNTESTED_DECISIONS, len(reach["untestedComplex"])))
        lines.append("  test calls into the assembly that match no key: %d" % len(reach["unmatchedTestCalls"]))
        lines.extend(format_risk(reach.get("risk"), top=10))
    cover = result.get("coverage")
    if cover:
        lines.extend(format_coverage(cover))
    else:
        lines.append("  coverage: not measured (no snapshot; scripts/arch/coverage.py --write-snapshot)")
    gates = result["gates"]
    lines.append("  ratchet sets: %d cross-type static writes, %d methods at complexity >= %d (%d in the watch band"
                 " from %d), %d unreached" % (
                     len(gates["crossTypeStaticWrites"]), len(gates["complexity50"]), COMPLEXITY_GATE,
                     len(gates["complexityWatch"]), COMPLEXITY_GATE - COMPLEXITY_WATCH_BAND,
                     len(gates["uncalledInAssembly"])))
    return "\n".join(lines)


# --- loading ----------------------------------------------------------------------


def load_facts(path, modules=archview.DEFAULT_MODULES, source=None):
    """A facts model from a facts .json, an .il dump or an assembly.

    `modules` None builds it with no module map (the test assembly).
    Returns (facts, tooling).
    """
    path = Path(path)
    tooling = ()
    rules = None
    if modules is not None:
        rules, tooling, _forbidden, _allowed = archview.load_rules(modules)
    if path.suffix.lower() == ".json":
        facts = json.loads(path.read_text(encoding="utf-8"))
        if facts.get("schema") != ilfacts.SCHEMA:
            raise ValueError("%s is %r, not %s (re-run ilfacts.py --json)" % (path, facts.get("schema"), ilfacts.SCHEMA))
        return facts, tooling
    table = None
    if rules is not None:
        table = ilfacts.source_type_modules(source or (archview.REPO_ROOT / archview.DEFAULT_SOURCE), rules)
    return ilfacts.build(ilgraph.load_parsed(path), table), tooling


def coverage_module():
    """scripts/arch/coverage.py. Its name is also the name of the coverage.py
    package on PyPI; a process that imported that one first gets a clear error
    here instead of a wrong module."""
    import coverage  # noqa: PLC0415

    if getattr(coverage, "SCHEMA", None) != "parsek-coverage/1":
        raise ImportError("`coverage` resolved to %s, not scripts/arch/coverage.py"
                          % getattr(coverage, "__file__", "?"))
    return coverage


def history_churn(facts, source=None, repo_root=None, since=None):
    """{since, commits, typeCommits} for the risk table: the Atlas history's
    window and sweep rule (archview.git_log_lines / parse_history), each file's
    commits, and each type's busiest file through a scan of the source tree.
    None when git gives nothing (no repository, no git)."""
    since = since or archview.default_since()
    text = archview.git_log_lines(repo_root or archview.REPO_ROOT, since)
    if not text:
        return None
    commits = archview.parse_history(text)["commits"]
    table = ilfacts.source_type_files(source or (archview.REPO_ROOT / archview.DEFAULT_SOURCE))
    return {"since": since, "commits": len(commits),
            "typeCommits": type_commits(facts["types"], table, file_commit_counts(commits))}


def report_from_paths(facts_path, tests_path=None, modules=archview.DEFAULT_MODULES, source=None,
                      coverage_path=None, history=True):
    """The report, with `headless` (the declared headless modules' leaks) and `lifetimes`
    (lifetimes.summary) added: the declared static
    lifetimes over the same facts, testWrittenUntagged measured only when
    `tests_path` is the test assembly itself (a .dll). The coverage snapshot is
    read from `coverage_path` (default scripts/arch/coverage_snapshot.json; a
    missing file is "not measured"), and with the test facts the risk table
    reads the git history unless `history` is False."""
    import lifetimes  # noqa: PLC0415 (it imports this module)

    import headless  # noqa: PLC0415

    coverage_snapshot = coverage_module()
    facts, tooling = load_facts(facts_path, modules, source)
    tests = load_facts(tests_path, None)[0] if tests_path else None
    snapshot = coverage_snapshot.load_snapshot(coverage_path or coverage_snapshot.DEFAULT_SNAPSHOT)
    churn = history_churn(facts, source) if (tests is not None and history) else None
    out = report(facts, tooling, load_config(), tests, headless.declared(headless.load_headless(modules)),
                 snapshot, churn)
    tests_dll = tests_path if tests_path and str(tests_path).lower().endswith(".dll") else None
    out["lifetimes"] = lifetimes.summary(lifetimes.analyse_facts(facts, tooling, tests_dll))
    return out


def main(argv=None):
    parser = argparse.ArgumentParser(description="Analyses and ratchets over the compiled-IL facts.")
    parser.add_argument("input", help="Parsek.dll, an ikdasm .il dump, or a facts .json (ilfacts.py --json)")
    parser.add_argument("--tests", help="Parsek.Tests.dll (or .il / facts .json): test reach and the test-only split")
    parser.add_argument("--json", help="write the report here")
    parser.add_argument("--write-pins", action="store_true", help="re-pin facts_pins.toml from this input")
    parser.add_argument("--pins", default=str(DEFAULT_PINS))
    parser.add_argument("--modules", default=str(archview.DEFAULT_MODULES))
    parser.add_argument("--source", default=str(archview.REPO_ROOT / archview.DEFAULT_SOURCE))
    parser.add_argument("--coverage", help="a coverage snapshot (default scripts/arch/coverage_snapshot.json)")
    parser.add_argument("--no-history", action="store_true",
                        help="do not read git history for the risk table's commits column")
    args = parser.parse_args(argv)
    if args.write_pins and re.search(r"[\\/]Release[\\/]", str(args.input)):
        parser.error("pin from the Debug build CI measures, not a Release build")
    result = report_from_paths(args.input, args.tests, args.modules, args.source, args.coverage,
                               not args.no_history)
    print(format_report(result))
    if args.json:
        Path(args.json).write_text(json.dumps(result, indent=1, sort_keys=True, default=sorted) + "\n",
                                   encoding="utf-8")
    pins_path = Path(args.pins)
    if args.write_pins:
        import datetime  # noqa: PLC0415

        pins_path.write_text(render_pins(result["gates"], Path(args.input).name,
                                         datetime.date.today().isoformat()), encoding="utf-8")
        print("Wrote %s" % pins_path)
    elif pins_path.is_file():
        problems = ratchet_problems(result["gates"], load_pins(pins_path), gate_details(result))
        for line in problems or ["ratchets hold"]:
            print("  " + line)
        for line in ratchet_slack(result["gates"], load_pins(pins_path)):
            print("  slack: " + line)
    return 0


if __name__ == "__main__":
    sys.exit(main())
