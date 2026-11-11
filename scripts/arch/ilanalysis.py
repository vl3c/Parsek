#!/usr/bin/env python3
"""Analyses over the compiled-IL facts (ilfacts.py), and the ratchets on them.

Five reports, each a pure function of the facts model (and, for test reach and
the test-only split, the test assembly's facts):

- static state: per module and owning type, reassignable statics, static
  readonly collections whose contents some method other than a static
  constructor changes, and statics written (or mutated) from another
  production outer type;
- complexity: decisions + 1 per method with the compiler-generated code folded
  in, ranked, and counted at 20 / 50 / 100;
- game coupling: a method can reach the game when it names a live type or
  member of the game assemblies, or calls a method that can (interface and
  virtual calls reach every implementation and override in the assembly);
  per module, the share of instructions in such methods;
- uncalled code: production methods nothing calls once the roots are honoured,
  split by test reach when the test facts are given, with each root rule's
  weight (how many methods it alone kept);
- test reach: per module, the share of production methods (and instructions)
  a unit test calls directly, and the complex methods none does.

facts.toml (beside modules.toml) holds every list the rules read: the inert
game types, the root attributes and names, the mutating members.

The ratchets bind only facts that are stable between builds (call sets,
static write sets, per-method decisions) and are pinned in facts_pins.toml
from the Debug DLL CI builds:

    python3 scripts/arch/ilanalysis.py Source/Parsek/bin/Debug/Parsek.dll                 # report
    python3 scripts/arch/ilanalysis.py Parsek.dll --tests Parsek.Tests.dll --json r.json   # with test reach
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
        writers = set(field.get("writers", ())) | set(field.get("addressTakenBy", ()))
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


def complexity_gate_names(report):
    """`Type::Name` of every production method at or above COMPLEXITY_GATE (overloads merge)."""
    return sorted({_key_type(r["key"]) + "::" + _key_name(r["key"])
                   for r in report["methods"] if r["complexity"] >= COMPLEXITY_GATE})


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


def direct_game_refs(entry, coupling):
    """The live game types and members a method's body or signature names."""
    found = set()
    for scope in ("body", "signature"):
        found.update(t for t in entry["external"][scope] if _live_type(t, coupling))
    found.update(m for m in entry.get("externalMembers", ()) if _live_member(m, coupling))
    return found


def game_reach(index, config):
    """{method key: True} for every method that can reach the game, and the direct set."""
    coupling = config["coupling"]
    not_followed = tuple(coupling.get("not_followed", ()))
    direct = {key for key, entry in index.methods.items() if direct_game_refs(entry, coupling)}
    reach = set(direct)
    pending = list(direct)

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
        for caller in callers:
            if caller in index.methods and caller not in reach:
                reach.add(caller)
                pending.append(caller)
    return direct, reach


def coupling_report(index, config):
    direct, reach = game_reach(index, config)
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


def uncalled(index, config, test_calls=None):
    """Production methods with no caller in the assembly once the roots are honoured.

    Returns {"rootedBy": {rule: [keys]}, "uncalled": [keys], and, when
    `test_calls` (production keys a test calls, dispatch already expanded) is
    given, "noCaller" and "testOnly"}. A method with a caller is never counted
    under a rule, so each rule's list is what that rule alone keeps.
    """
    rules = config["uncalled"]
    ignored = rules["ignored_attributes"]
    patch_methods = set(rules["patch_methods"])
    unity = set(rules["unity_messages"])
    object_members = set(rules["object_members"])
    strings_by_outer = defaultdict(set)
    every_string = set()
    for entry in index.methods.values():
        strings = entry.get("strings", ())
        strings_by_outer[ilfacts.outermost(entry["type"])].update(strings)
        every_string.update(strings)

    hooks = set(rules["patch_hooks"])

    def is_patch_type(path):
        # A [HarmonyPatch] type, or one handed to Harmony by `typeof` (named
        # by another type) that declares a static patch hook.
        if any(a in rules["patch_type_attributes"] for a in _type_attributes(index, path, ignored)):
            return True
        return bool(index.types[path]["namedByOthers"]) and any(
            index.methods[k]["name"] in hooks and "static" in index.methods[k]["flags"]
            for k in index.by_type.get(path, ()))

    candidates = [
        key for key, entry in index.methods.items()
        if index.production(entry["type"]) and not index.callers.get(key)
    ]
    rooted = {}

    def used(key):
        return bool(index.callers.get(key)) or key in rooted

    def first_rules(key):
        entry = index.methods[key]
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

    for key in candidates:
        rule = first_rules(key)
        if rule:
            rooted[key] = rule
    for key in candidates:
        if key not in rooted and any(used(slot) for slot in index.slots.get(key, ())):
            rooted[key] = "dispatch from a used slot"
    for key in candidates:
        entry = index.methods[key]
        if key in rooted:
            continue
        slot_like = "abstract" in entry["flags"] or index.types[entry["type"]]["kind"] == "interface"
        if slot_like and any(used(impl) for impl in index.implementers.get(key, ())):
            rooted[key] = "slot with a used implementation"
    for key in candidates:
        if key in rooted:
            continue
        siblings = index.accessor_siblings(key)
        if siblings and any(used(s) for s in siblings):
            rooted[key] = "accessor of a used property"
    for key in candidates:
        if key in rooted:
            continue
        entry = index.methods[key]
        path = entry["type"]
        if entry["name"] != ".ctor":
            continue
        type_entry = index.types[path]
        others_used = any(used(k) for k in index.by_type.get(path, ()) if index.methods[k]["name"] != ".ctor")
        if (others_used or type_entry["namedByOthers"] or index.engine_base(path)
                or index.is_attribute_type(path) or _type_attributes(index, path, ignored)):
            rooted[key] = "constructor of a used type"
    for key in candidates:
        if key in rooted:
            continue
        entry = index.methods[key]
        name = (explicit_parts(entry["name"]) or (None, entry["name"]))[1]
        member = name
        for prefix in ACCESSOR_PREFIXES:
            if name.startswith(prefix):
                member = name[len(prefix):]
        names = {name, member}
        if names & strings_by_outer[ilfacts.outermost(entry["type"])]:
            rooted[key] = "named in a string of its own type"
        elif names & every_string:
            rooted[key] = "named in a string elsewhere"
        elif entry["compilerGenerated"]:
            rooted[key] = "compiler-generated"

    by_rule = {rule: [] for rule in ROOT_RULES}
    for key, rule in rooted.items():
        by_rule[rule].append(key)
    left = sorted(key for key in candidates if key not in rooted)
    out = {"rootedBy": {rule: sorted(keys) for rule, keys in by_rule.items()}, "uncalled": left}
    if test_calls is not None:
        out["testOnly"] = [key for key in left if key in test_calls]
        out["noCaller"] = [key for key in left if key not in test_calls]
    return out


def is_test_hook(key, suffixes):
    name = _key_name(key)
    return next((suffix for suffix in suffixes if name.endswith(suffix)), None)


def uncalled_gate_keys(report, config):
    """The ratchet's set: no caller in the assembly after the roots, test hooks by name left out.

    Read from Parsek.dll alone, so it holds methods only a test calls too
    (that is production code the game never runs; a hook named as one does
    not count).
    """
    suffixes = config["uncalled"]["test_hook_suffixes"]
    return sorted(key for key in report["uncalled"] if not is_test_hook(key, suffixes))


# --- e. test reach ---------------------------------------------------------------


def test_called(index, tests_facts):
    """Production keys a test method calls directly (calls on a slot reach its implementations)."""
    prefix = (index.facts.get("assembly") or ilgraph.ROOT_NAMESPACE) + "|"
    called = set()
    unmatched = set()
    for entry in tests_facts["methods"].values():
        for call in entry.get("crossAssemblyCalls", ()):
            if not call.startswith(prefix):
                continue
            key = call[len(prefix):]
            if key in index.methods:
                called.add(key)
            else:
                unmatched.add(key)
    for key in list(called):
        called |= index.implementers.get(key, set())
    return called, unmatched


def test_reach_report(index, called):
    rows = defaultdict(Counter)
    untested = []
    for key, entry in index.methods.items():
        if not index.production(entry["type"]):
            continue
        row = rows[index.module(entry["type"])]
        n = size(entry)
        row["methods"] += 1
        row["instructions"] += n
        if key in called:
            row["reachedMethods"] += 1
            row["reachedInstructions"] += n
        elif complexity(entry) - 1 >= UNTESTED_DECISIONS:
            untested.append({"key": key, "module": index.module(entry["type"]),
                             "decisions": complexity(entry) - 1, "instructions": n})
    untested.sort(key=lambda r: (-r["decisions"], r["key"]))
    out = {}
    for module, row in rows.items():
        out[module] = dict(row, methodShare=row["reachedMethods"] / (row["methods"] or 1),
                           instructionShare=row["reachedInstructions"] / (row["instructions"] or 1))
    return {"byModule": out, "untestedComplex": untested}


# --- the whole report -------------------------------------------------------------


def report(facts, tooling=(), config=None, tests_facts=None):
    """Every analysis over one facts model, JSON-ready (sets become sorted lists)."""
    config = config or load_config()
    index = Index(facts, tooling)
    test_calls = None
    unmatched = []
    if tests_facts is not None:
        test_calls, unmatched_set = test_called(index, tests_facts)
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
        "gates": {
            "crossTypeStaticWrites": sorted(statics["crossTypeWrites"]),
            "complexity50": complexity_gate_names(cx),
            "uncalledInAssembly": uncalled_gate_keys(dead, config),
        },
    }
    if test_calls is not None:
        hooks = Counter()
        for key in dead["testOnly"]:
            hooks[is_test_hook(key, suffixes) or "other"] += 1
        out["uncalled"]["testOnlyBySuffix"] = dict(hooks)
        out["testReach"] = dict(test_reach_report(index, test_calls), unmatchedTestCalls=unmatched)
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
        "# A bound may only fall. Re-pin in the PR that lowers one; re-pin to raise one",
        "# only when the change is deliberate, and say why in the PR.",
    ]
    if source or date:
        lines.append("# Pinned from %s %s" % (source, date))
    for name in ("crossTypeStaticWrites", "uncalledInAssembly", "complexity50"):
        keys = gates[name]
        lines.append("")
        lines.append("[%s]" % name)
        if name != "complexity50":
            lines.append("bound = %d" % len(keys))
        lines.append("keys = [")
        lines.extend("    %s," % json.dumps(key) for key in keys)
        lines.append("]")
    return "\n".join(lines) + "\n"


def ratchet_problems(gates, pins, details=None, names=None):
    """Why the measured sets break the pins (empty when they hold).

    Counted sets (cross-type static writes, uncalled) red only when the count
    rises above the bound; the complexity set reds when a method not pinned
    enters it. `details` maps a key to a short explanation (its writers, its
    complexity) for the message; `names` limits the check to those ratchets.
    """
    details = details or {}
    problems = []

    def describe(keys):
        return "; ".join("%s%s" % (key, (" (" + details[key] + ")") if key in details else "") for key in keys)

    for name, label in (("crossTypeStaticWrites", "statics written by another production type"),
                        ("uncalledInAssembly", "production methods with no caller in the assembly")):
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
    new = sorted(set(gates["complexity50"]) - set(pins["complexity50"]["keys"]))
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
    gone = set(pins["complexity50"]["keys"]) - set(gates["complexity50"])
    if gone:
        out.append("complexity50: %d pinned methods no longer at the gate" % len(gone))
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


def format_report(result, top=15):
    lines = ["CODE FACTS (%s)" % result["assembly"]]
    totals = result["statics"]["totals"]
    lines.append(
        "  statics: %d reassignable, %d readonly collections (%d mutated outside a static constructor),"
        " %d reassignable written by another production type, %d collections mutated by one"
        % (totals.get("reassignable", 0), totals.get("readonlyCollections", 0), totals.get("mutatedCollections", 0),
           totals.get("writtenElsewhere", 0), totals.get("mutatedElsewhere", 0))
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
    lines.append("  no caller in the assembly after the roots: %d methods, %d instructions" % (
        len(dead["uncalled"]), sum(dead["instructions"].values())))
    for rule in ROOT_RULES:
        if dead["rootedBy"].get(rule):
            lines.append("    kept by %-34s %d" % (rule + ":", len(dead["rootedBy"][rule])))
    if "noCaller" in dead:
        lines.append("    no caller at all: %d; called only from the test assembly: %d %s" % (
            len(dead["noCaller"]), len(dead["testOnly"]), dict(sorted(dead["testOnlyBySuffix"].items()))))
    if result["testReach"]:
        reach = result["testReach"]
        lines.append("  called directly by a unit test, per module (methods / instructions):")
        for module, row in sorted(reach["byModule"].items(), key=lambda kv: -kv[1]["instructions"]):
            lines.append("    %-14s %4s / %4s" % (module, _pct(row["methodShare"]), _pct(row["instructionShare"])))
        lines.append("  methods with >= %d decisions no unit test calls: %d" % (
            UNTESTED_DECISIONS, len(reach["untestedComplex"])))
        lines.append("  test calls into the assembly that match no key: %d" % len(reach["unmatchedTestCalls"]))
    gates = result["gates"]
    lines.append("  ratchet sets: %d cross-type static writes, %d methods at complexity >= %d, %d uncalled" % (
        len(gates["crossTypeStaticWrites"]), len(gates["complexity50"]), COMPLEXITY_GATE,
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


def report_from_paths(facts_path, tests_path=None, modules=archview.DEFAULT_MODULES, source=None):
    facts, tooling = load_facts(facts_path, modules, source)
    tests = load_facts(tests_path, None)[0] if tests_path else None
    return report(facts, tooling, load_config(), tests)


def main(argv=None):
    parser = argparse.ArgumentParser(description="Analyses and ratchets over the compiled-IL facts.")
    parser.add_argument("input", help="Parsek.dll, an ikdasm .il dump, or a facts .json (ilfacts.py --json)")
    parser.add_argument("--tests", help="Parsek.Tests.dll (or .il / facts .json): test reach and the test-only split")
    parser.add_argument("--json", help="write the report here")
    parser.add_argument("--write-pins", action="store_true", help="re-pin facts_pins.toml from this input")
    parser.add_argument("--pins", default=str(DEFAULT_PINS))
    parser.add_argument("--modules", default=str(archview.DEFAULT_MODULES))
    parser.add_argument("--source", default=str(archview.REPO_ROOT / archview.DEFAULT_SOURCE))
    args = parser.parse_args(argv)
    if args.write_pins and re.search(r"[\\/]Release[\\/]", str(args.input)):
        parser.error("pin from the Debug build CI measures, not a Release build")
    result = report_from_paths(args.input, args.tests, args.modules, args.source)
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
