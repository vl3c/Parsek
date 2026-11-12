#!/usr/bin/env python3
"""Per-member facts read from the compiled assembly, for reports and later gates.

ilgraph.py reads an `ikdasm` dump into a type-to-type graph. The same parse
(one pass, `ilgraph.parse_il`) also records what this module turns into a
facts model keyed by member:

- types by full IL path: namespace plus nesting, quotes and generic arity
  dropped (`Parsek.Logistics.RouteEndpointTransfer/VesselIdentity`), so two
  types that share a short name stay apart. Each carries its kind, header
  flags, base type, interfaces, custom attributes, how many other types name
  it in IL (`namedByOthers`) and the module modules.toml assigns its source
  file (`module: null` when the source scan cannot say).
- fields by `Type::name`: static, initonly, literal, compiler-generated, the
  property it backs (`<X>k__BackingField`), event backing, attributes, and
  for static fields the methods that read, write (`stsfld`) and take the
  address of (`ldsflda`) them, the address-takers that assign `default`
  through it (`defaultedBy`: `ldsflda` then `initobj`), plus `receiverCalls`
  (`method|member`: an outside-assembly call, or `[]` for an element store,
  whose receiver is the static's value or a value read out of it, or a
  parameter the static is passed to a directly called in-assembly method in;
  ilgraph's StaticReceiverTracker; the evidence for "mutates its contents")
  and `directReceiverCalls` (the subset whose receiver is the static's own
  value; the evidence a clearing call empties it).
- methods by `Type::Name(param types)`, ``Name``N`` for a generic method, the
  parameter types normalized so a declaration and a call site print the same
  text (`Parsek.Foo::Short(string)`; a conversion operator also carries its
  return type). Each carries flags, custom attributes, instruction count,
  decision count (conditional branches + distinct `switch` targets + catch
  and filter handlers), the in-assembly members it calls (`call`,
  `callvirt`, `newobj`, `ldftn`, `ldvirtftn`), static field reads / writes /
  address-takes, the external types its body, locals and signature name
  (`assembly|type`, the BCL left out), the external members it names
  (`externalMembers`, `assembly|Type::Name`), its identifier-shaped string
  literals (`strings`: reflection by name and `nameof`), and calls into
  another assembly's root-namespace types (`Parsek|Parsek.Foo::Bar(int32)`
  from the test DLL). Code generated but owned by no single method credits its
  calls and static access to `Type::(generated)`, and its type `unattributed`
  bucket records that code's external types (`external`: body and locals) and
  members (`externalMembers`), so a reader of game coupling can treat the
  bucket as one more method of its source type.

Compiler-generated code (lambda bodies in `<>c`, closure classes, local
functions, iterator and async state machines) is folded into the source
method that created it. Attribution follows IL references: the source method
that calls, constructs (`newobj`) or takes the address of (`ldftn`) a
generated member, or touches a field of a generated type, owns it, and so
does everything that member reaches inside its own generated type (an
iterator's `MoveNext` through its constructor). The `<Name>` inside a mangled
name is used only when no reference exists and the name is unique on its
type. Generated members are never keys in the output: their ordinals
(`<Run>d__594`) shift whenever a method is added above them and differ between
Debug and Release. Shared members that belong to no single method (the `<>c`
cache's constructors) are counted on their source type under `unattributed`.
A method's `instructions` / `decisions` are its own body; `generated` adds the
code folded into it, and its calls, statics and external types include it.

Stable versus build-sensitive (Debug against Release on b5877e51f): keys,
static field sets and method attributes are identical, calls differ in one
method of 22,046, decisions in 0.3% of methods, locals' external types in
2.4%, and Release has 22% fewer instructions. Gate nothing on instruction
counts, and measure on the Debug DLL CI builds.

    python3 scripts/arch/ilfacts.py Source/Parsek/bin/Debug/Parsek.dll          # summary
    python3 scripts/arch/ilfacts.py dump.il --json facts.json                     # facts model
    python3 scripts/arch/ilfacts.py Parsek.Tests.dll --modules none               # no module map

Everything except `main` is pure. Plain stdlib.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
import time
from collections import Counter, defaultdict
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import archview  # noqa: E402
import ilgraph  # noqa: E402

SCHEMA = "parsek-ilfacts/2"
CALL_OPCODES = frozenset("call callvirt newobj ldftn ldvirtftn".split())
STATIC_FIELD_OPCODES = {"ldsfld": "staticReads", "stsfld": "staticWrites", "ldsflda": "staticAddress"}
BACKING_FIELD_RE = re.compile(r"^<(.+)>k__BackingField$")
MANGLED_SOURCE_RE = re.compile(r"^<([^<>]+)>")
COMPILER_GENERATED_ATTRIBUTE = "mscorlib|System.Runtime.CompilerServices.CompilerGeneratedAttribute"
# Field types the summary counts as collections (on the type text only).
COLLECTION_TYPE_RE = re.compile(
    r"System\.Collections\.(?:Generic\.|Concurrent\.)?\w*(?:List|Dictionary|Set|Queue|Stack|Collection)"
    r"|System\.Text\.StringBuilder|\[\]"
)
NAMESPACE_RE = re.compile(r"\bnamespace\s+([A-Za-z_][\w.]*)\s*([{;])")
DELEGATE_RE = re.compile(r"\bdelegate\s+[^;{}()=]*?\b([A-Z][A-Za-z0-9_]*)\s*(?:<[^;{}()]*>)?\s*\(")
UNATTRIBUTED = "(generated)"


def is_generated_path(path):
    return any(segment.startswith("<") for segment in path.split("/"))


def is_root_path(path):
    return bool(path) and (path == ilgraph.ROOT_NAMESPACE or path.startswith(ilgraph.ROOT_NAMESPACE + "."))


def outermost(path):
    return path.split("/")[0]


def member_key(type_path, name, generic_arity, params, return_type=None):
    """`Type::Name(p1,p2)`; ``Name``N`` for a generic method; a conversion operator adds `:ret`."""
    text = "%s::%s" % (type_path, name)
    if generic_arity:
        text += "``%d" % generic_arity
    text += "(%s)" % ",".join(params or ())
    if return_type and name in ("op_Implicit", "op_Explicit"):
        text += ":" + return_type
    return text


def _method_key(method):
    return member_key(
        method["type"], method["name"], len(method["generics"]), method["params"], method["returnType"]
    )


def is_generated_method(method):
    return is_generated_path(method["type"]) or (method["name"] or "").startswith("<")


def decisions(method):
    return method["branches"] + method["switchTargets"] + method["catches"]


def type_kind(header):
    words = header.split()
    if "interface" in words:
        return "interface"
    base = header.split(" extends ", 1)[1].split()[0] if " extends " in header else ""
    if base.endswith("System.Enum"):
        return "enum"
    if base.endswith("System.ValueType"):
        return "struct"
    if base.endswith("System.MulticastDelegate"):
        return "delegate"
    return "class"


def _declared(text):
    text = re.sub(r"^\s*(?:class|valuetype)\s+", "", text.strip())
    assembly, path, _generic = ilgraph.split_declaring(text)
    return path if assembly is None else assembly + "|" + path


def _base_type(header):
    if " extends " not in header:
        return None
    return _declared(header.split(" extends ", 1)[1].split(" implements ", 1)[0])


def _interfaces(header):
    """The interfaces a type header lists (an interface's own header lists its parents)."""
    if " implements " not in header:
        return []
    rest = header.split(" implements ", 1)[1]
    return sorted({_declared(item) for item in ilgraph._split_top_level(rest, ",") if item.strip()})


# --- module map from the source tree ----------------------------------------


def _namespace_spans(stripped):
    """[(start, end, name)] for block and file-scoped namespace declarations."""
    spans = []
    for match in NAMESPACE_RE.finditer(stripped):
        if match.group(2) == ";":
            spans.append((match.start(), len(stripped), match.group(1)))
            continue
        open_index = match.end() - 1
        depth = 0
        end = len(stripped)
        for i in range(open_index, len(stripped)):
            if stripped[i] == "{":
                depth += 1
            elif stripped[i] == "}":
                depth -= 1
                if depth == 0:
                    end = i
                    break
        spans.append((match.start(), end, match.group(1)))
    return spans


def _namespace_at(spans, offset):
    return ".".join(name for start, end, name in spans if start <= offset < end)


def declared_type_paths(stripped):
    """Full IL-style paths (`Ns.Outer/Inner`) of the types a stripped C# file declares.

    Classes, structs, interfaces and enums with their nesting (names shorter
    than four characters are skipped, as archview does), plus top-level
    delegates, which a nested-type lookup never needs.
    """
    spans = _namespace_spans(stripped)
    declarations = archview.type_declarations(stripped)
    paths = []
    for declaration in declarations:
        chain = [declaration["name"]]
        parent = declaration["parent"]
        while parent is not None:
            chain.append(declarations[parent]["name"])
            parent = declarations[parent]["parent"]
        chain.reverse()
        namespace = _namespace_at(spans, declaration["start"])
        head = (namespace + "." if namespace else "") + chain[0]
        paths.append("/".join([head] + chain[1:]))
    for match in DELEGATE_RE.finditer(stripped):
        inside = any(d["span"][0] < match.start() < d["span"][1] for d in declarations)
        if not inside:
            namespace = _namespace_at(spans, match.start())
            paths.append((namespace + "." if namespace else "") + match.group(1))
    return paths


_DECLARED_CACHE = {}


def declared_by_file(source_root):
    """[(source-relative path, type paths it declares)] for every scanned .cs file.

    One scan serves the module table and the file table; it is kept per
    process, keyed by the root and every file's size and modification time,
    so a changed tree is scanned again."""
    source_root = Path(source_root)
    rels = archview.iter_source_files(source_root)
    stamps = []
    for rel in rels:
        stat = (source_root / rel).stat()
        stamps.append((rel.as_posix(), stat.st_size, stat.st_mtime_ns))
    key = (str(source_root.resolve()), tuple(stamps))
    if key not in _DECLARED_CACHE:
        rows = []
        for rel in rels:
            text = (source_root / rel).read_text(encoding="utf-8-sig", errors="replace")
            rows.append((rel.as_posix(), declared_type_paths(archview.strip_comments_and_strings(text))))
        _DECLARED_CACHE.clear()
        _DECLARED_CACHE[key] = rows
    return _DECLARED_CACHE[key]


def source_type_modules(source_root, rules):
    """{type path: set of modules} for every type the source tree declares."""
    table = defaultdict(set)
    for rel, paths in declared_by_file(source_root):
        module = archview.assign_module(rel, rules)
        if module is None:
            continue
        for path in paths:
            table[path].add(module)
    return dict(table)


GENERIC_ARITY_RE = re.compile(r"`\d+")


def source_type_files(source_root):
    """{type path: sorted source-relative files declaring it} (a partial type has several).

    The same scan as `source_type_modules`, so a type the facts place in a
    module is found here too; the paths are posix, relative to `source_root`
    (the form archview.parse_history gives a changed file)."""
    table = defaultdict(set)
    for rel, paths in declared_by_file(source_root):
        for path in paths:
            table[path].add(rel)
    return {path: sorted(files) for path, files in table.items()}


def files_for(path, table):
    """The source files of an IL type path: its own entry, else its nearest
    declared enclosing type's; a generic arity (``Foo`1``) is matched without
    it. () when nothing matches."""
    parts = path.split("/")
    for size in range(len(parts), 0, -1):
        head = "/".join(parts[:size])
        for candidate in (head, GENERIC_ARITY_RE.sub("", head)):
            files = table.get(candidate)
            if files:
                return tuple(files)
    return ()


def module_for(path, table):
    """The module of an IL type path: its own entry, else its nearest declared
    enclosing type's. None when nothing matches or the match is in two modules."""
    parts = path.split("/")
    for size in range(len(parts), 0, -1):
        modules = table.get("/".join(parts[:size]))
        if modules:
            return next(iter(modules)) if len(modules) == 1 else None
    return None


# --- the facts model ---------------------------------------------------------


def _attribute_generated(methods, generated, refs_in, type_refs, by_source_name):
    """{generated index: (owner index or None, how)}; how is reference / name / ambiguous / none."""
    deps = {}
    for g in generated:
        own_type = methods[g]["type"]
        incoming = refs_in.get(g, set())
        if is_generated_path(own_type) and all(methods[r]["type"] == own_type for r in incoming):
            incoming = incoming | type_refs.get(own_type, set())
        deps[g] = incoming
    owners = {g: set() for g in generated}
    changed = True
    while changed:
        changed = False
        for g in generated:
            found = set()
            for d in deps[g]:
                if d in owners:
                    found |= owners[d]
                else:
                    found.add(d)
            if not found <= owners[g]:
                owners[g] |= found
                changed = True
    result = {}
    for g in generated:
        if len(owners[g]) == 1:
            result[g] = (next(iter(owners[g])), "reference")
            continue
        if len(owners[g]) > 1:
            result[g] = (None, "ambiguous")
            continue
        method = methods[g]
        source_type = ilgraph.source_owner(method["type"])
        candidates = []
        for text in [method["name"] or ""] + list(reversed(method["type"].split("/"))):
            match = MANGLED_SOURCE_RE.match(text)
            if match:
                candidates = by_source_name.get((source_type, match.group(1)), [])
                break
        if len(candidates) == 1:
            result[g] = (candidates[0], "name")
        else:
            result[g] = (None, "none")
    return result


def _passed_static_mutations(methods, resolved):
    """({id(method body): {(field key, member)}}, the same for direct receivers):
    a static the method passes to an in-assembly call whose parameter the callee
    (or a callee it passes the parameter on to, to a fixpoint) is the receiver
    of an outside call through. Direct only when every hop passed the value
    itself, not one read out of it, and the last callee called on the
    parameter's own value. Only a directly resolved callee counts: a call
    through an interface or virtual slot does not reach the implementations'
    bodies."""
    reached = (defaultdict(lambda: defaultdict(set)), defaultdict(lambda: defaultdict(set)))  # any, direct
    for i, method in enumerate(methods):
        for parameter, member in method.get("paramCalls", ()):
            reached[0][i][parameter].add(member)
        for parameter, member in method.get("paramDirectCalls", ()):
            reached[1][i][parameter].add(member)
    changed = True
    while changed:
        changed = False
        for i, method in enumerate(methods):
            for parameter, position, callee_parameter, derived in method.get("paramArgs", ()):
                target = resolved.get((i, position))
                if target is None or target == i:
                    continue
                # Every direct call is in `any` too, so a derived value only adds to `any`.
                gains = [(reached[0], reached[0].get(target, {}).get(callee_parameter))]
                if not derived:
                    gains.append((reached[1], reached[1].get(target, {}).get(callee_parameter)))
                for into, members in gains:
                    if members and not members <= into[i][parameter]:
                        into[i][parameter] |= members
                        changed = True
    out_any, out_direct = {}, {}
    for i, method in enumerate(methods):
        found, direct = set(), set()
        for field_key, position, callee_parameter, derived in method.get("staticArgs", ()):
            target = resolved.get((i, position))
            if target is None:
                continue
            for member in reached[0].get(target, {}).get(callee_parameter, ()):
                found.add((field_key, member))
            if not derived:
                for member in reached[1].get(target, {}).get(callee_parameter, ()):
                    direct.add((field_key, member))
        if found:
            out_any[id(method)] = found
        if direct:
            out_direct[id(method)] = direct
    return out_any, out_direct


def build(parsed, type_modules=None):
    """The facts model from one parse_il result (read, never changed).

    `type_modules` is source_type_modules' table, or None to leave every
    module null (an assembly whose source is not under the module map).
    """
    methods = parsed["methods"]
    info = parsed.get("typeInfo", {})
    root = [i for i, m in enumerate(methods) if m["type"] and is_root_path(outermost(m["type"]))]
    generated = [i for i in root if is_generated_method(methods[i])]
    generated_set = set(generated)
    source = [i for i in root if i not in generated_set]

    key_of = {}
    by_key = {}
    collisions = []
    for i in source:
        key = _method_key(methods[i])
        if key in by_key:
            collisions.append(key)
            continue
        key_of[i] = key
        by_key[key] = i
    loose = defaultdict(list)
    exact = {}
    for i in root:
        method = methods[i]
        exact[(method["type"], method["name"], len(method["generics"]), method["params"])] = i
        loose[(method["type"], method["name"], len(method["params"] or ()))].append(i)
    by_source_name = defaultdict(list)
    for i in source:
        by_source_name[(methods[i]["type"], methods[i]["name"])].append(i)

    # Resolve every in-assembly member reference once.
    resolved = {}  # (method index, ref position) -> target method index
    unresolved = defaultdict(set)
    refs_in = defaultdict(set)
    type_refs = defaultdict(set)
    for i in root:
        method = methods[i]
        for position, (opcode, assembly, path, name, garity, params, ret) in enumerate(method["refs"]):
            if assembly is not None or not is_root_path(outermost(path)):
                continue
            if is_generated_path(path) and path != method["type"]:
                type_refs[path].add(i)
            if params is None or opcode not in CALL_OPCODES:
                continue
            target = exact.get((path, name, garity, params))
            if target is None:
                candidates = loose.get((path, name, len(params)), [])
                if len(candidates) == 1:
                    target = candidates[0]
            if target is None:
                unresolved[i].add(member_key(path, name, garity, params, ret))
                continue
            resolved[(i, position)] = target
            if target != i:
                refs_in[target].add(i)

    attribution = _attribute_generated(methods, generated, refs_in, type_refs, by_source_name)
    passed_mutations, passed_direct = _passed_static_mutations(methods, resolved)

    def module_of(path):
        if type_modules is None:
            return None
        return module_for(ilgraph.source_owner(path) or path, type_modules)

    # Types (source types only; generated ones fold into their source type).
    types = {}
    for path, type_entry in info.items():
        if not is_root_path(outermost(path)) or is_generated_path(path):
            continue
        header = type_entry["header"]
        types[path] = {
            "kind": type_kind(header),
            "flags": sorted(
                {w for w in header.split(" extends ")[0].split() if w in ilgraph.CLASS_FLAGS}
            ),
            "base": _base_type(header),
            "generic": bool(type_entry["generics"]),
            "attributes": sorted(set(type_entry["attributes"])),
            "interfaces": _interfaces(header),
            "module": module_of(path),
            "namedByOthers": 0,
            "unattributed": {"methods": 0, "instructions": 0, "decisions": 0, "calls": [],
                             "external": {"body": [], "locals": []}, "externalMembers": []},
        }
    for owner, owner_uses in parsed.get("uses", {}).items():
        named = set()
        for use in owner_uses:
            target = use[1]
            if target != owner and target in types:
                named.add(target)
        for target in named:
            types[target]["namedByOthers"] += 1

    # Methods: each source method with the generated code folded into it.
    out_methods = {}
    folded_from = defaultdict(list)
    counts = Counter()
    for g in generated:
        owner, how = attribution[g]
        counts["generatedMethods"] += 1
        counts["generatedInstructions"] += len(methods[g]["ops"])
        counts["generatedBy:" + how] += 1
        if owner is not None and owner in key_of:
            folded_from[owner].append(g)
        else:
            source_type = ilgraph.source_owner(methods[g]["type"])
            if source_type in types:
                bucket = types[source_type]["unattributed"]
                bucket["methods"] += 1
                bucket["instructions"] += len(methods[g]["ops"])
                bucket["decisions"] += decisions(methods[g])
            counts["unattributedInstructions"] += len(methods[g]["ops"])

    def owner_key(index):
        """The output key that stands for method `index` (generated -> its owner)."""
        if index in key_of:
            return key_of[index]
        if index in attribution:
            owner = attribution[index][0]
            if owner is not None and owner in key_of:
                return key_of[owner]
            return ilgraph.source_owner(methods[index]["type"]) + "::" + UNATTRIBUTED
        return None

    field_access = defaultdict(lambda: defaultdict(set))
    field_mutations = defaultdict(set)
    field_direct = defaultdict(set)

    def note_statics(body, key, statics):
        for position, (opcode, assembly, path, name, _garity, params, _ret) in enumerate(body["refs"]):
            if assembly is None and opcode in STATIC_FIELD_OPCODES and params is None:
                if is_root_path(outermost(path)) and not is_generated_path(path):
                    field_key = path + "::" + name
                    if statics is not None:
                        statics[STATIC_FIELD_OPCODES[opcode]].add(field_key)
                    field_access[field_key][STATIC_FIELD_OPCODES[opcode]].add(key)
        for field_key, member in set(body.get("staticCalls", ())) | passed_mutations.get(id(body), set()):
            if not is_generated_path(_type_of_key(field_key)):
                field_mutations[field_key].add((key, member))
        for field_key, member in set(body.get("staticDirectCalls", ())) | passed_direct.get(id(body), set()):
            if not is_generated_path(_type_of_key(field_key)):
                field_direct[field_key].add((key, member))
        for field_key in body.get("staticDefaults", ()):
            if not is_generated_path(_type_of_key(field_key)):
                field_access[field_key]["staticDefaults"].add(key)

    # Generated code no single source method owns still calls, reads and
    # writes: credit it to its source type's `Type::(generated)` key, so no
    # caller or writer is lost.
    for g in generated:
        owner, _how = attribution[g]
        if owner is not None and owner in key_of:
            continue
        source_type = ilgraph.source_owner(methods[g]["type"])
        if source_type not in types:
            continue
        pseudo = source_type + "::" + UNATTRIBUTED
        bucket = types[source_type]["unattributed"]
        targets = set(bucket["calls"])
        for position in range(len(methods[g]["refs"])):
            target = resolved.get((g, position))
            if target is not None:
                target_key = owner_key(target)
                if target_key is not None and target_key != pseudo:
                    targets.add(target_key)
        bucket["calls"] = sorted(targets)
        for scope in ("body", "locals"):
            bucket["external"][scope] = sorted(set(bucket["external"][scope]) | methods[g]["external"][scope])
        outside = set(bucket["externalMembers"])
        for opcode, assembly, path, name, _garity, params, _ret in methods[g]["refs"]:
            if assembly is None or ilgraph.BCL_ASSEMBLY_RE.match(assembly):
                continue
            if not (params is not None and opcode in CALL_OPCODES and is_root_path(outermost(path))):
                outside.add("%s|%s::%s" % (assembly, path, name))
        bucket["externalMembers"] = sorted(outside)
        note_statics(methods[g], pseudo, None)

    for i in source:
        if i not in key_of:
            continue
        method = methods[i]
        key = key_of[i]
        members = [i] + folded_from.get(i, [])
        calls = set()
        statics = {"staticReads": set(), "staticWrites": set(), "staticAddress": set()}
        external = {"body": set(), "locals": set(), "signature": set()}
        cross = set()
        outside = set()
        strings = set()
        for m in members:
            body = methods[m]
            strings |= body.get("strings", set())
            note_statics(body, key, statics)
            for scope in external:
                if m == i or scope != "signature":
                    external[scope] |= body["external"][scope]
            for position, (opcode, assembly, path, name, garity, params, ret) in enumerate(body["refs"]):
                if assembly is not None:
                    if params is not None and opcode in CALL_OPCODES and is_root_path(outermost(path)):
                        cross.add(assembly + "|" + member_key(path, name, garity, params, ret))
                    elif not ilgraph.BCL_ASSEMBLY_RE.match(assembly):
                        outside.add("%s|%s::%s" % (assembly, path, name))
                    continue
                if opcode in STATIC_FIELD_OPCODES and params is None:
                    continue
                target = resolved.get((m, position))
                if target is None:
                    continue
                target_key = owner_key(target)
                if target_key is not None and target_key != key:
                    calls.add(target_key)
        own_decisions = decisions(method)
        entry = {
            "type": method["type"],
            "name": method["name"],
            "flags": sorted(method["flags"]),
            "attributes": sorted(set(method["attributes"])),
            "compilerGenerated": COMPILER_GENERATED_ATTRIBUTE in method["attributes"],
            "instructions": len(method["ops"]),
            "decisions": own_decisions,
            "calls": sorted(calls),
            "unresolvedCalls": sorted(set().union(*(unresolved.get(m, set()) for m in members))),
            "staticReads": sorted(statics["staticReads"]),
            "staticWrites": sorted(statics["staticWrites"]),
            "staticAddress": sorted(statics["staticAddress"]),
            "external": {scope: sorted(values) for scope, values in external.items()},
            "crossAssemblyCalls": sorted(cross),
            "externalMembers": sorted(outside),
            "strings": sorted(strings),
        }
        if folded_from.get(i):
            each = [decisions(methods[g]) for g in folded_from[i]]
            entry["generated"] = {
                "methods": len(each),
                "instructions": sum(len(methods[g]["ops"]) for g in folded_from[i]),
                "decisions": sum(each),
                "decisionsEach": sorted(each, reverse=True),
            }
        out_methods[key] = entry
        counts["unresolvedCalls"] += len(entry["unresolvedCalls"])

    # Fields of source types.
    out_fields = {}
    for record in parsed.get("fields", []):
        path = record["type"]
        if path not in types:
            continue
        key = path + "::" + record["name"]
        flags = record["flags"]
        backing = BACKING_FIELD_RE.match(record["name"])
        entry = {
            "fieldType": record["fieldType"],
            "static": "static" in flags,
            "initonly": "initonly" in flags,
            "literal": "literal" in flags,
            "compilerGenerated": record["name"].startswith("<")
            or COMPILER_GENERATED_ATTRIBUTE in record["attributes"],
            "backingFor": backing.group(1) if backing else None,
            "event": record["name"] in info[path]["events"],
            "attributes": sorted(set(record["attributes"])),
        }
        if entry["static"] and not entry["literal"]:
            access = field_access.get(key, {})
            entry["readers"] = sorted(access.get("staticReads", ()))
            entry["writers"] = sorted(access.get("staticWrites", ()))
            entry["addressTakenBy"] = sorted(access.get("staticAddress", ()))
            # The `ldsflda` + `initobj` pairs among them: `x = default` / `x = null`
            # on a value-type or nullable static, a write with no `stsfld`.
            entry["defaultedBy"] = sorted(access.get("staticDefaults", ()))
            # Outside-assembly calls (or `[]` element stores) on the value or
            # a value read out of it (ilgraph.StaticReceiverTracker, with its
            # window fallback): the evidence for "mutates its contents".
            entry["receiverCalls"] = sorted("%s|%s" % pair for pair in field_mutations.get(key, ()))
            # The subset whose receiver is the static's own value, followed
            # down the stack: the evidence a clearing call empties it.
            entry["directReceiverCalls"] = sorted("%s|%s" % pair for pair in field_direct.get(key, ()))
        out_fields[key] = entry

    counts.update({
        "types": len(types),
        "methods": len(out_methods),
        "fields": len(out_fields),
        "keyCollisions": len(collisions),
        "outsideRootNamespace": len(methods) - len(root),
        "typesWithoutModule": sum(1 for t in types.values() if t["module"] is None),
    })
    return {
        "schema": SCHEMA,
        "assembly": parsed.get("assembly"),
        "counts": dict(sorted(counts.items())),
        "keyCollisions": sorted(collisions),
        "types": dict(sorted(types.items())),
        "fields": dict(sorted(out_fields.items())),
        "methods": dict(sorted(out_methods.items())),
    }


# --- summary -----------------------------------------------------------------


def _type_of_key(key):
    return key.split("::", 1)[0]


def summary(facts, tooling=(), every_type=False):
    """Headline numbers (a dict) over the production types: module set and not tooling.

    `every_type` counts every type instead, for an assembly built without a
    module map (the test DLL).
    """
    types = facts["types"]

    def production(type_path):
        entry = types.get(ilgraph.source_owner(type_path) or type_path)
        if entry is None:
            return False
        return every_type or (entry["module"] is not None and entry["module"] not in tooling)

    statics = {
        key: field for key, field in facts["fields"].items()
        if field["static"] and not field["literal"] and production(_type_of_key(key))
        and not key.split("::", 1)[1].startswith("<>")
    }
    mutable = {k: f for k, f in statics.items() if not f["initonly"]}
    collections = {k: f for k, f in statics.items() if f["initonly"] and COLLECTION_TYPE_RE.search(f["fieldType"])}

    def cross_writers(key, methods_list):
        own = outermost(_type_of_key(key))
        return [
            m for m in methods_list
            if outermost(_type_of_key(m)) != own and production(_type_of_key(m))
        ]

    setter_callers = defaultdict(set)
    for method_key, method in facts["methods"].items():
        for callee in method["calls"]:
            if "::set_" in callee:
                setter_callers[callee.split("(", 1)[0]].add(method_key)
    cross_stsfld = {k for k, f in mutable.items() if cross_writers(k, f["writers"])}
    cross_addr = {k for k, f in mutable.items() if cross_writers(k, f["addressTakenBy"])}
    cross_setter = set()
    for k, f in mutable.items():
        if f["backingFor"]:
            setter = _type_of_key(k) + "::set_" + f["backingFor"]
            if cross_writers(k, setter_callers.get(setter, ())):
                cross_setter.add(k)

    prod_methods = {k: m for k, m in facts["methods"].items() if production(m["type"])}
    own_cx = [m["decisions"] + 1 for m in prod_methods.values()]
    folded_cx = [m["decisions"] + m.get("generated", {}).get("decisions", 0) + 1 for m in prod_methods.values()]
    # The measurement review's rule: generated methods as methods of their own,
    # static constructors left out.
    each_cx = [m["decisions"] + 1 for m in prod_methods.values() if m["name"] != ".cctor"]
    for m in prod_methods.values():
        each_cx.extend(d + 1 for d in m.get("generated", {}).get("decisionsEach", ()))
    instructions = sum(m["instructions"] for m in prod_methods.values())
    generated_instr = sum(m.get("generated", {}).get("instructions", 0) for m in prod_methods.values())
    unattributed_instr = sum(
        entry["unattributed"]["instructions"] for path, entry in types.items() if production(path)
    )

    def at_least(values):
        return {threshold: sum(1 for v in values if v >= threshold) for threshold in (20, 50, 100)}

    return {
        "assembly": facts["assembly"],
        "scope": "every type" if every_type else "production types",
        "crossAssemblyMembers": len({c for m in prod_methods.values() for c in m["crossAssemblyCalls"]}),
        "productionTypes": sum(1 for path in types if production(path)),
        "typesWithoutModule": facts["counts"].get("typesWithoutModule", 0),
        "reassignableStatics": len(mutable),
        "reassignableStaticBackingFields": sum(1 for f in mutable.values() if f["backingFor"]),
        "staticReadonlyCollections": len(collections),
        "crossTypeWritersStsfld": len(cross_stsfld),
        "crossTypeWritersWithLdsflda": len(cross_stsfld | cross_addr),
        "crossTypeWritersWithSetters": len(cross_stsfld | cross_addr | cross_setter),
        "complexityOwnBody": at_least(own_cx),
        "complexityFolded": at_least(folded_cx),
        "complexityEveryIlMethod": at_least(each_cx),
        "productionInstructions": instructions + generated_instr + unattributed_instr,
        "generatedInstructions": generated_instr + unattributed_instr,
        "unattributedInstructions": unattributed_instr,
        "counts": facts["counts"],
    }


def format_summary(numbers):
    lines = ["IL FACTS (%s, %s)" % (numbers["assembly"], numbers["scope"])]
    lines.append(
        "  types counted: %d (types with no module: %d)"
        % (numbers["productionTypes"], numbers["typesWithoutModule"])
    )
    lines.append(
        "  reassignable statics: %d (%d static auto-property backing fields)"
        % (numbers["reassignableStatics"], numbers["reassignableStaticBackingFields"])
    )
    lines.append("  static readonly collections: %d" % numbers["staticReadonlyCollections"])
    lines.append(
        "  reassignable statics written by another production type: %d stsfld, %d with ldsflda,"
        " %d with static auto-property setters"
        % (
            numbers["crossTypeWritersStsfld"],
            numbers["crossTypeWritersWithLdsflda"],
            numbers["crossTypeWritersWithSetters"],
        )
    )
    for label, key in (
        ("own body", "complexityOwnBody"),
        ("generated code folded in", "complexityFolded"),
        ("every IL method but .cctor, generated ones on their own", "complexityEveryIlMethod"),
    ):
        values = numbers[key]
        lines.append(
            "  methods with complexity (decisions + 1) >= 20 / 50 / 100, %s: %d / %d / %d"
            % (label, values[20], values[50], values[100])
        )
    total = numbers["productionInstructions"] or 1
    lines.append(
        "  instructions in compiler-generated code: %d of %d (%.1f%%), %d not attributable to one method"
        % (
            numbers["generatedInstructions"],
            numbers["productionInstructions"],
            100.0 * numbers["generatedInstructions"] / total,
            numbers["unattributedInstructions"],
        )
    )
    lines.append(
        "  distinct members called in another assembly's %s types: %d"
        % (ilgraph.ROOT_NAMESPACE, numbers["crossAssemblyMembers"])
    )
    counts = numbers["counts"]
    lines.append(
        "  whole assembly: %d types, %d methods, %d fields; generated methods %d (by reference %d,"
        " by name %d, ambiguous %d, none %d); unresolved calls %d; key collisions %d"
        % (
            counts.get("types", 0), counts.get("methods", 0), counts.get("fields", 0),
            counts.get("generatedMethods", 0), counts.get("generatedBy:reference", 0),
            counts.get("generatedBy:name", 0), counts.get("generatedBy:ambiguous", 0),
            counts.get("generatedBy:none", 0), counts.get("unresolvedCalls", 0),
            counts.get("keyCollisions", 0),
        )
    )
    return "\n".join(lines)


def main(argv=None):
    parser = argparse.ArgumentParser(description="Per-member facts from a compiled assembly.")
    parser.add_argument("input", help="an assembly (disassembled with ikdasm) or an ikdasm .il dump")
    parser.add_argument("--json", help="write the facts model here ('-' for stdout)")
    parser.add_argument("--summary", action="store_true", help="print the summary (default without --json)")
    parser.add_argument("--source", default=str(archview.REPO_ROOT / archview.DEFAULT_SOURCE), help="module source root")
    parser.add_argument(
        "--modules", default=str(archview.DEFAULT_MODULES),
        help="modules.toml, or 'none' to leave every module null (e.g. for Parsek.Tests.dll)",
    )
    args = parser.parse_args(argv)
    started = time.time()
    parsed = ilgraph.load_parsed(args.input)
    parsed_at = time.time()
    tooling = ()
    table = None
    if args.modules != "none":
        rules, tooling, _forbidden, _allowed = archview.load_rules(args.modules)
        table = source_type_modules(args.source, rules)
    facts = build(parsed, table)
    if args.json:
        text = json.dumps(facts, indent=1, sort_keys=True)
        if args.json == "-":
            sys.stdout.write(text + "\n")
        else:
            Path(args.json).write_text(text + "\n", encoding="utf-8")
    if args.summary or not args.json:
        print(format_summary(summary(facts, tooling, every_type=table is None)))
        print(
            "  time: %.1fs disassemble + parse, %.1fs facts" % (parsed_at - started, time.time() - parsed_at)
        )
    return 0


if __name__ == "__main__":
    sys.exit(main())
