#!/usr/bin/env python3
"""Semantic type edges for the architecture map, read from the compiled assembly.

archview.py finds references by matching identifiers in source text. That scan
counts a name, so it cannot tell a call that only passes through a forwarder
(a method whose body is one call to another type, as `RecordingStore.CanRewind`
was to `RewindLauncher.CanRewind` until W4c deleted it) from a real dependency
on the type that holds the forwarder. Code can therefore lower the text residue by adding a forwarder,
and raise it by deleting one, without changing what calls what.

This module reads the IL the compiler emitted (an `ikdasm` dump of Parsek.dll,
the same disassembler scripts/refactor/il_compare.py uses) and returns, per
source type, the in-assembly types its code really uses:

- the declaring type of every member an instruction names (call, callvirt,
  newobj, ldftn, field loads and stores, ldtoken of a member), plus the type
  arguments of a generic declaring type or generic method;
- every type an instruction names as its operand (castclass, isinst, box,
  newarr, initobj, ldtoken, ...);
- the types in the type's own declarations: base type and interfaces, field
  types, its own method signatures, locals, custom attributes, overrides,
  properties and events.

The parameter and return types of a CALLED member are not counted: they are the
callee's signature, the same way the text scan does not count a type the
caller never names.

Compiler-generated nested types (closures, iterator and async state machines,
`<>c`) are credited to the nearest source type that encloses them, so a lambda
body's references belong to the type that wrote the lambda.

Forwarders. A method is a forwarder when its body is argument loads, at most
some reads of its own type's fields or parameterless getters (a service
accessor), and exactly ONE member operation on another in-assembly type,
then `ret` (a Debug build's `stloc / br / ldloc` return shuffle and `nop`s are
ignored). Constructors and virtual or abstract methods never are (a call to
them can dispatch elsewhere, and a derived constructor calling its base is not
a forward). With `resolve_forwarders=True`, a call to a forwarder is credited to
the type the forward chain ends in instead of the forwarder's type, but only
when every overload of that name and arity on that type is a forwarder ending
in the same type. Deleting such a forwarder and calling the target directly
then leaves the graph unchanged, and adding one cannot hide an edge.

Everything except `run_ikdasm` is pure (text in, data out), so the tests drive
it with inline IL. Plain stdlib.
"""

from __future__ import annotations

import re
import shutil
import subprocess
from collections import defaultdict
from pathlib import Path

ROOT_NAMESPACE = "Parsek"

# An in-assembly type token: a dotted name under the root namespace, an
# optional generic arity, then `/Nested` segments (quoted when generated).
TYPE_TOKEN_RE = re.compile(
    r"(?<![\w.'`:$])" + ROOT_NAMESPACE + r"(?:\.[A-Za-z_]\w*)*(?:`\d+)?"
    r"(?:/(?:'[^'\n]+'|[A-Za-z_]\w*)(?:`\d+)?)*"
)
CLASS_FLAGS = frozenset(
    """public private auto ansi sealed beforefieldinit abstract interface sequential
    explicit nested assembly family famandassem famorassem serializable specialname
    rtspecialname import unicode autochar static windowsruntime""".split()
)
METHOD_FLAGS = frozenset(
    """public private assembly family famandassem famorassem privatescope hidebysig
    static instance virtual final newslot abstract specialname rtspecialname
    pinvokeimpl strict reqsecobj unmanagedexp explicit default vararg""".split()
)
# Instructions whose operand is a member reference `[ret] Decl::name(args)`.
MEMBER_OPCODES = frozenset(
    """call callvirt newobj ldftn ldvirtftn jmp ldfld ldflda stfld ldsfld ldsflda
    stsfld""".split()
)
FIELD_LOADS = frozenset("ldfld ldsfld".split())
# What the one foreign operation of a forwarder may be: a call, a construction,
# or a static field (a property that forwards to another type's static). An
# instance field read on an argument is a getter, not a forward.
FORWARD_TARGET_OPCODES = frozenset("call callvirt newobj ldsfld ldsflda stsfld".split())
# Instructions a forwarder may hold besides its one member operation.
FORWARDER_LOADS_RE = re.compile(r"^(?:ldarg|ldarga|ldc\.|ldnull$|ldstr$)")
# Debug-build return shuffle and padding, ignored when reading a forwarder.
FORWARDER_IGNORED_RE = re.compile(r"^(?:nop|stloc|ldloc(?!a)|br(?:\.s)?$)")
INSTRUCTION_RE = re.compile(r"^\s*IL_[0-9a-fA-F]+:\s+(\S+)\s*(.*)$")
STRING_RE = re.compile(r'"(?:[^"\\\n]|\\.)*"')


def strip_strings_and_comments(line):
    """Drop string literals and `//` comments from one line of IL text."""
    line = STRING_RE.sub('""', line)
    cut = line.find("//")
    return line if cut < 0 else line[:cut]


def canonical_path(token):
    """`Parsek.Foo`1/'<>c__3`1'` -> `Parsek.Foo/<>c__3`: quotes and arities dropped."""
    parts = []
    for segment in token.split("/"):
        segment = segment.strip("'")
        segment = re.sub(r"`\d+$", "", segment)
        parts.append(segment)
    return "/".join(parts)


def is_generated(segment):
    return segment.startswith("<")


def source_owner(path):
    """The nearest enclosing path that is not compiler-generated (None if none)."""
    parts = path.split("/")
    while parts and is_generated(parts[-1]):
        parts.pop()
    if not parts or not parts[0].startswith(ROOT_NAMESPACE):
        return None
    return "/".join(parts)


def short_name(path):
    """`Parsek.Logistics.Outer/Inner` -> `Inner`; `Parsek.Foo` -> `Foo`."""
    return path.split("/")[-1].split(".")[-1]


def type_tokens(text):
    """Canonical owner paths of every in-assembly type token in `text`."""
    found = []
    for match in TYPE_TOKEN_RE.finditer(text):
        owner = source_owner(canonical_path(match.group(0)))
        if owner is not None:
            found.append(owner)
    return found


def _top_level_find(text, needle, start=0):
    depth = 0
    i = start
    n = len(text)
    while i < n:
        char = text[i]
        if char in "<([":
            depth += 1
        elif char in ">)]":
            depth = max(0, depth - 1)
        elif depth == 0 and text.startswith(needle, i):
            return i
        i += 1
    return -1


def _arity(params):
    """Number of parameters in a `(...)` parameter list's inside text."""
    if not params.strip():
        return 0
    depth = 0
    count = 1
    for char in params:
        if char in "<([":
            depth += 1
        elif char in ">)]":
            depth = max(0, depth - 1)
        elif char == "," and depth == 0:
            count += 1
    return count


def _matching_close(text, open_index):
    depth = 0
    pairs = {"(": ")", "<": ">", "[": "]"}
    opener = text[open_index]
    closer = pairs[opener]
    for i in range(open_index, len(text)):
        if text[i] == opener:
            depth += 1
        elif text[i] == closer:
            depth -= 1
            if depth == 0:
                return i
    return len(text) - 1


def parse_member_operand(operand):
    """Split a member operand into (declaring type text, name, generic args text, arity).

    `arity` is None for a field. Returns None when the operand names no
    `Type::member` (a bare method token never appears in this position).
    """
    sep = _top_level_find(operand, "::")
    if sep < 0:
        return None
    # The declaring type ends at `::`; walk back over it, across any generic
    # argument list, to the whitespace that separates it from the return type.
    i = sep
    depth = 0
    while i > 0:
        char = operand[i - 1]
        if char in ">)]":
            depth += 1
        elif char in "<([":
            depth -= 1
        elif depth == 0 and char in " \t":
            break
        i -= 1
    declaring = operand[i:sep].strip()
    rest = operand[sep + 2 :]
    match = re.match(r"\s*('[^']*'|[^\s(<]+)", rest)
    if not match:
        return None
    name = match.group(1).strip("'")
    j = match.end()
    generic_args = ""
    if j < len(rest) and rest[j] == "<":
        close = _matching_close(rest, j)
        generic_args = rest[j + 1 : close]
        j = close + 1
    while j < len(rest) and rest[j] in " \t":
        j += 1
    arity = None
    if j < len(rest) and rest[j] == "(":
        close = _matching_close(rest, j)
        arity = _arity(rest[j + 1 : close])
    return declaring, name, generic_args, arity


def _class_name(header):
    """The type name on a `.class` header line (flags are lowercase keywords)."""
    text = header.strip()[len(".class") :].strip()
    for token in re.findall(r"'[^']*'(?:<[^>]*>)?|\S+", text):
        if token in CLASS_FLAGS:
            continue
        name = token
        if not name.startswith("'"):
            name = name.split("<", 1)[0]
        else:
            name = name[: name.rindex("'") + 1]
        return name
    return None


def _method_signature(header):
    """(name, arity, flags) of a `.method` header (all its lines joined)."""
    text = header.strip()[len(".method") :]
    open_index = -1
    depth = 0
    for i, char in enumerate(text):
        if char == "<":
            depth += 1
        elif char == ">":
            depth = max(0, depth - 1)
        elif char == "(" and depth == 0:
            open_index = i
            break
    if open_index < 0:
        return None, None, set()
    close = _matching_close(text, open_index)
    head = text[:open_index].rstrip()
    if head.endswith(">"):
        depth = 0
        k = len(head) - 1
        while k >= 0:
            if head[k] == ">":
                depth += 1
            elif head[k] == "<":
                depth -= 1
                if depth == 0:
                    break
            k -= 1
        head = head[:k].rstrip()
    match = re.search(r"('[^']*'|[\w.$<>`]+)$", head)
    name = match.group(1).strip("'") if match else None
    flags = {word for word in re.findall(r"\b[a-z]+\b", head) if word in METHOD_FLAGS}
    return name, _arity(text[open_index + 1 : close]), flags


def parse_il(text):
    """Parse an ikdasm dump into per-type uses and per-method bodies.

    Returns {"uses": {owner path: [use, ...]}, "methods": [method, ...]}.
    A use is ("type", path, origin) or ("member", declaring path, name, arity,
    opcode); origin says where the type was named (header, signature, local,
    field, custom, override, property, event, param, operand, generic, catch).
    Field declarations also record their literal (const) names under
    "literals" {owner path: set of names}, and every source type path seen is
    listed under "types" (the generic ones also under "genericTypes").
    A method is {"type": path, "owner": owner path, "name", "arity", "flags",
    "ops": [(opcode, use or None), ...]}.
    """
    uses = defaultdict(list)
    literals = defaultdict(set)
    type_paths = []
    generic_paths = []
    methods = []
    class_stack = []  # (brace depth inside the class, canonical path)
    depth = 0
    pending_class = None
    pending_method = None
    method = None
    method_depth = None
    directive = None  # (kind, text) of a multi-line directive or instruction
    lines = text.splitlines()

    def current_path():
        return class_stack[-1][1] if class_stack else None

    def add_type_uses(chunk, origin):
        path = current_path()
        owner = source_owner(path) if path else None
        if owner is None:
            return
        for target in type_tokens(chunk):
            uses[owner].append(("type", target, origin))

    def flush_instruction():
        nonlocal directive
        if directive is None:
            return
        kind, opcode, operand = directive
        directive = None
        if kind == "locals":
            add_type_uses(operand, "local")
            return
        if kind == "decl":
            add_type_uses(operand, opcode)
            if opcode == "field" and re.search(r"\bliteral\b", operand):
                path = current_path()
                owner = source_owner(path) if path else None
                name_match = re.search(r"('[^']*'|\w+)\s*=", operand)
                if owner is not None and name_match:
                    literals[owner].add(name_match.group(1).strip("'"))
            return
        path = current_path()
        owner = source_owner(path) if path else None
        use = None
        if opcode in MEMBER_OPCODES or (opcode == "ldtoken" and "::" in operand):
            parsed = parse_member_operand(operand)
            if parsed is not None:
                declaring, name, generic_args, arity = parsed
                decl_owner = None
                head = declaring.split("<", 1)[0]
                generic_text = declaring[len(head) :]
                if head.strip().startswith(ROOT_NAMESPACE):
                    tokens = type_tokens(head)
                    if tokens:
                        decl_owner = tokens[0]
                if decl_owner is not None:
                    use = ("member", decl_owner, name, arity, opcode)
                if owner is not None:
                    if use is not None:
                        uses[owner].append(use)
                    for target in type_tokens(generic_text + " " + generic_args):
                        uses[owner].append(("type", target, "generic"))
        elif owner is not None and opcode != "ldstr":
            for target in type_tokens(operand):
                uses[owner].append(("type", target, "operand"))
        if method is not None:
            method["ops"].append((opcode, use))

    for raw in lines:
        line = strip_strings_and_comments(raw)
        stripped = line.strip()
        if not stripped:
            continue
        instruction = INSTRUCTION_RE.match(line)
        if instruction:
            flush_instruction()
            directive = ("instr", instruction.group(1), instruction.group(2))
            continue
        if directive is not None and directive[0] in ("instr", "locals", "decl") and not (
            stripped.startswith(".") or stripped in ("{", "}")
        ) and not stripped.startswith(("catch ", "finally", "fault", "filter", "IL_")):
            kind, opcode, operand = directive
            directive = (kind, opcode, operand + " " + stripped)
            continue
        flush_instruction()

        if pending_class is not None and stripped != "{":
            pending_class += " " + stripped
            continue
        if pending_method is not None and stripped != "{":
            pending_method += " " + stripped
            continue

        if stripped == "{":
            depth += 1
            if pending_class is not None:
                name = _class_name(pending_class.split(" extends ")[0].split(" implements ")[0])
                parent = current_path()
                if parent is None:
                    path = canonical_path(name)
                else:
                    path = parent + "/" + canonical_path(name)
                class_stack.append((depth, path))
                type_paths.append(path)
                if "`" in name:
                    generic_paths.append(path)
                # Base type, interfaces and generic constraints; the own name
                # is a self-reference and drops out.
                add_type_uses(pending_class, "header")
                pending_class = None
            elif pending_method is not None:
                name, arity, flags = _method_signature(pending_method)
                header = pending_method
                if name and "." in name.strip("."):
                    # An explicit interface implementation is named
                    # `Parsek.IFoo.Bar`; the interface is in the base list,
                    # and `Bar` is a method, not a type.
                    header = header.replace(name, " ")
                add_type_uses(header, "signature")
                path = current_path()
                method = {
                    "type": path,
                    "owner": source_owner(path) if path else None,
                    "name": name,
                    "arity": arity,
                    "flags": flags,
                    "ops": [],
                }
                methods.append(method)
                method_depth = depth
                pending_method = None
            continue
        if stripped == "}":
            if method is not None and method_depth == depth:
                method = None
                method_depth = None
            if class_stack and class_stack[-1][0] == depth:
                class_stack.pop()
            depth = max(0, depth - 1)
            continue
        if stripped.startswith(".class"):
            pending_class = stripped
            continue
        if stripped.startswith(".method"):
            pending_method = stripped
            continue
        if stripped.startswith(".locals"):
            directive = ("locals", None, stripped)
            continue
        if stripped.startswith((".field", ".custom", ".override", ".property", ".event", ".param type", ".interfaceimpl")):
            directive = ("decl", stripped.split()[0].lstrip("."), stripped)
            continue
        # .get / .set / .addon / .removeon name the type's own accessors;
        # .maxstack, .entrypoint, .try, catch handlers, labels carry no use
        # except a catch clause's exception type.
        if stripped.startswith(("catch ", "filter")):
            add_type_uses(stripped, "catch")
    flush_instruction()
    return {
        "uses": dict(uses),
        "methods": methods,
        "literals": {owner: sorted(names) for owner, names in literals.items()},
        "types": type_paths,
        "genericTypes": generic_paths,
    }


def forwarder_target(method):
    """The (declaring path, name, arity, opcode) a method forwards to, or None."""
    owner = method["owner"]
    if owner is None or method["type"] != owner:
        return None
    if method["name"] in (".ctor", ".cctor") or method["name"] is None:
        return None
    if method["flags"] & {"virtual", "abstract"}:
        return None
    target = None
    ops = [(op, use) for op, use in method["ops"] if not FORWARDER_IGNORED_RE.match(op)]
    if not ops or ops[-1][0] != "ret":
        return None
    for opcode, use in ops[:-1]:
        if use is None:
            if FORWARDER_LOADS_RE.match(opcode) or opcode == "pop":
                continue
            return None
        _kind, decl, name, arity, op = use
        if decl == owner:
            accessor = op in FIELD_LOADS or (
                op in ("call", "callvirt") and name.startswith("get_") and arity == 0
            )
            if accessor and target is None:
                continue
            return None
        if target is not None:
            return None
        if op not in FORWARD_TARGET_OPCODES:
            return None
        target = (decl, name, arity, op)
    return target


def forwarder_table(methods):
    """{(type path, name, arity): final target type path} for resolvable groups.

    A group is every method of one type with one name and arity (overloads the
    arity cannot separate). It resolves only when each member is a forwarder
    and every chain ends in the same type.
    """
    groups = defaultdict(list)
    for method in methods:
        if method["owner"] is None or method["type"] != method["owner"]:
            continue
        groups[(method["owner"], method["name"], method["arity"])].append(method)
    direct = {}
    for key, members in groups.items():
        targets = [forwarder_target(member) for member in members]
        if all(target is not None for target in targets):
            direct[key] = targets

    resolved = {}

    def final_type(key, seen):
        if key in resolved:
            return resolved[key]
        if key in seen or key not in direct:
            return None
        seen = seen | {key}
        ends = set()
        for decl, name, arity, _op in direct[key]:
            next_key = (decl, name, arity)
            nested = final_type(next_key, seen) if next_key in direct else None
            ends.add(nested if nested is not None else decl)
        return ends.pop() if len(ends) == 1 else None

    for key in direct:
        end = final_type(key, frozenset())
        if end is not None and end != key[0]:
            resolved[key] = end
    return resolved


def type_graph_from_il(parsed, graph_names, resolve_forwarders=True):
    """{type name: sorted names it uses} over `graph_names`, from parse_il output.

    Paths are mapped to the text model's node names by their short name, so a
    name the text model excludes (shorter than four characters, declared in two
    modules, tooling) is excluded here too, and same-named types in one module
    merge the way they do there.
    """
    table = forwarder_table(parsed["methods"]) if resolve_forwarders else {}
    graph = {name: set() for name in graph_names}
    for owner, owner_uses in parsed["uses"].items():
        source = short_name(owner)
        if source not in graph:
            continue
        found = graph[source]
        for use in owner_uses:
            if use[0] == "type":
                target_path = use[1]
            else:
                _kind, decl, name, arity, _op = use
                target_path = table.get((decl, name, arity), decl)
            target = short_name(target_path)
            if target != source and target in graph:
                found.add(target)
    return {name: sorted(refs) for name, refs in graph.items()}


def forwarder_calls(parsed, graph_names):
    """[(caller, forwarder type, member, resolved type)] for every resolved call.

    The rows the forwarder rule rewrites, deduplicated, for the report.
    """
    table = forwarder_table(parsed["methods"])
    rows = set()
    for owner, owner_uses in parsed["uses"].items():
        source = short_name(owner)
        if source not in graph_names:
            continue
        for use in owner_uses:
            if use[0] != "member":
                continue
            key = (use[1], use[2], use[3])
            if key in table and short_name(use[1]) != source:
                rows.add((source, short_name(use[1]), use[2], short_name(table[key])))
    return sorted(rows)


def run_ikdasm(dll_path):
    """Disassemble an assembly with ikdasm (mono-devel) and return the text."""
    tool = shutil.which("ikdasm")
    if tool is None:
        raise RuntimeError("ikdasm not found on PATH (install mono-devel)")
    result = subprocess.run(
        [tool, str(dll_path)], capture_output=True, text=True, errors="replace", check=False
    )
    if result.returncode != 0:
        raise RuntimeError("ikdasm failed on %s: %s" % (dll_path, result.stderr.strip()[:400]))
    return result.stdout


def load_il(path):
    """IL text for a .dll (disassembled) or a ready .il dump (read)."""
    path = Path(path)
    if path.suffix.lower() == ".il":
        return path.read_text(encoding="utf-8", errors="replace")
    return run_ikdasm(path)
