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
# `pinvokeimpl("user32.dll" winapi)` before the return type of a P/Invoke
# declaration and `marshal( bool)` after a marshalled return or parameter
# type: their parentheses are not the parameter list.
NON_PARAMETER_PARENS_RE = re.compile(r"\b(?:pinvokeimpl|marshal)\s*\([^)]*\)")
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
    full = parse_member_signature(operand)
    if full is None:
        return None
    return full[0], full[1], full[2], full[3]


def parse_member_signature(operand):
    """parse_member_operand plus (parameter list text, return type text).

    The parameter text is None for a field; the return type text is what
    precedes the declaring type (the field type for a field), with the
    `instance` / `explicit` calling-convention words still in it.
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
    params = None
    if j < len(rest) and rest[j] == "(":
        close = _matching_close(rest, j)
        params = rest[j + 1 : close]
        arity = _arity(params)
    return declaring, name, generic_args, arity, params, operand[:i]


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
    text = NON_PARAMETER_PARENS_RE.sub(" ", header.strip()[len(".method") :])
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


# --- member facts (read by ilfacts.py) ---------------------------------------
#
# The same walk that builds the type uses also records, per method, what
# ilfacts.py needs to key members exactly: the normalized parameter type list,
# every member operand with its exact declaring type (generated segments kept,
# so a lambda in `<>c` is not credited to the type around it), decision counts,
# custom attributes and the external types the body, locals and signature name.

CONDITIONAL_BRANCH_RE = re.compile(
    r"^(?:beq|bge|bgt|ble|blt|bne|brtrue|brfalse|brnull|brinst|brzero)(?:\.un)?(?:\.s)?$"
)
SWITCH_TARGET_RE = re.compile(r"\bIL_[0-9a-fA-F]+\b")
# A switch target continuation line: `IL_02a1,` or the last one, `IL_0268)`.
SWITCH_CONTINUATION_RE = re.compile(r"^IL_[0-9a-fA-F]+\s*[,)]")
# An external type reference: `[asm]Ns.Type/Nested`, the assembly name quoted
# when it is not an identifier (`['Assembly-CSharp']Part`).
EXTERNAL_TYPE_RE = re.compile(
    r"\[('[^'\]]+'|[A-Za-z_][\w.\-]*)\]([A-Za-z_][\w.`]*(?:/(?:'[^'\n]+'|[A-Za-z_][\w`]*))*)"
)
BCL_ASSEMBLY_RE = re.compile(r"^(?:mscorlib|netstandard|System(?:\..*)?|Microsoft\.CSharp)$")
PARAM_ATTRIBUTE_RE = re.compile(r"\[(?:in|out|opt)\]\s*")
ASSEMBLY_PREFIX_RE = re.compile(r"\[(?:'[^'\]]+'|[A-Za-z_][\w.\-]*)\](?=[A-Za-z_'])")
TYPE_KEYWORD_RE = re.compile(r"\b(?:class|valuetype)\s+")
MODIFIER_RE = re.compile(r"\s*\bmod(?:req|opt)\s*\(")
GENERIC_REF_RE = re.compile(r"(!!?)([A-Za-z_]\w*)")


def _split_top_level(text, separator):
    """Split on `separator` outside (), <>, [] and quotes."""
    parts = []
    depth = 0
    quoted = False
    start = 0
    for i, char in enumerate(text):
        if char == "'":
            quoted = not quoted
        elif quoted:
            continue
        elif char in "<([":
            depth += 1
        elif char in ">)]":
            depth = max(0, depth - 1)
        elif depth == 0 and (char == separator or (separator == " " and char in " \t")):
            parts.append(text[start:i])
            start = i + 1
    parts.append(text[start:])
    return [part for part in (p.strip() for p in parts) if part]


def _drop_modifiers(text):
    """Remove `modreq(...)` / `modopt(...)` custom modifiers from a type text."""
    while True:
        match = MODIFIER_RE.search(text)
        if not match:
            return text
        close = _matching_close(text, match.end() - 1)
        text = text[: match.start()] + text[close + 1 :]


def normalize_type_text(text, type_generics=(), method_generics=()):
    """One parameter or return type in a form that is equal at a declaration and a call.

    Drops `class` / `valuetype`, assembly prefixes, quotes, custom modifiers
    and whitespace, and turns named generic parameters (`!T`, `!!T`, which a
    declaration prints) into the positional form a call site prints (`!0`).
    """
    text = _drop_modifiers(PARAM_ATTRIBUTE_RE.sub("", NON_PARAMETER_PARENS_RE.sub(" ", text)))
    text = TYPE_KEYWORD_RE.sub("", text)
    text = ASSEMBLY_PREFIX_RE.sub("", text)
    text = text.replace("'", "")

    def positional(match):
        names = method_generics if match.group(1) == "!!" else type_generics
        name = match.group(2)
        return match.group(1) + (str(names.index(name)) if name in names else name)

    if "!" in text:
        text = GENERIC_REF_RE.sub(positional, text)
    return re.sub(r"\s+", "", text)


_NORMALIZED_PARAMS = {}


def normalize_params(text, type_generics=(), method_generics=(), named=False):
    """Tuple of normalized parameter types from a `(...)` list's inside text.

    `named` is True for a method declaration, whose parameters carry names
    (`class Parsek.Recording rec`); a call operand's do not.
    """
    if text is None:
        return None
    cache_key = (text, type_generics, method_generics, named)
    cached = _NORMALIZED_PARAMS.get(cache_key)
    if cached is not None:
        return cached
    result = []
    for param in _split_top_level(text, ","):
        param = _drop_modifiers(PARAM_ATTRIBUTE_RE.sub("", NON_PARAMETER_PARENS_RE.sub(" ", param)))
        tokens = [t for t in _split_top_level(param, " ") if t not in ("class", "valuetype")]
        if named and len(tokens) >= 2:
            tokens = tokens[:-1]
        result.append(normalize_type_text(" ".join(tokens), type_generics, method_generics))
    result = tuple(result)
    _NORMALIZED_PARAMS[cache_key] = result
    return result


def split_declaring(declaring):
    """(assembly or None, exact canonical path, generic argument text) of a declaring type.

    `class Parsek.Foo`1<!0>` -> (None, "Parsek.Foo", "!0");
    `Parsek.Foo/'<>c'` -> (None, "Parsek.Foo/<>c", "") (a `<` inside quotes is
    part of the name, never the start of the generic arguments);
    `['Assembly-CSharp']Part` -> ("Assembly-CSharp", "Part", "").
    """
    text = TYPE_KEYWORD_RE.sub("", declaring.strip(), count=1).strip()
    assembly = None
    if text.startswith("["):
        close = text.find("]")
        assembly = text[1:close].strip("'")
        text = text[close + 1 :]
    quoted = False
    cut = len(text)
    for i, char in enumerate(text):
        if char == "'":
            quoted = not quoted
        elif char == "<" and not quoted:
            cut = i
            break
    generic = text[cut + 1 : _matching_close(text, cut)] if cut < len(text) else ""
    return assembly, canonical_path(text[:cut].strip()), generic


def external_types(text):
    """{"assembly|type"} named in `text`, the BCL assemblies left out."""
    found = set()
    if "[" not in text:
        return found
    for match in EXTERNAL_TYPE_RE.finditer(text):
        assembly = match.group(1).strip("'")
        if BCL_ASSEMBLY_RE.match(assembly):
            continue
        found.add(assembly + "|" + canonical_path(match.group(2)))
    return found


def _generic_param_names(text):
    """Names in a `<...>` generic parameter list's inside text (constraints dropped)."""
    names = []
    for item in _split_top_level(text, ","):
        match = re.search(r"('[^']*'|[A-Za-z_]\w*)\s*$", item)
        if match:
            names.append(match.group(1).strip("'"))
    return tuple(names)


def _class_generics(header, name):
    """Generic parameter names declared on a `.class` header after its `name` token."""
    index = header.find(name)
    if index < 0:
        return ()
    after = index + len(name)
    if after < len(header) and header[after] == "<":
        return _generic_param_names(header[after + 1 : _matching_close(header, after)])
    return ()


def _method_header(header):
    """(generic parameter names, parameter list text, return type text) of a `.method` header."""
    text = NON_PARAMETER_PARENS_RE.sub(" ", header.strip()[len(".method") :])
    open_index = -1
    depth = 0
    quoted = False
    for i, char in enumerate(text):
        if char == "'":
            quoted = not quoted
        elif quoted:
            continue
        elif char == "<":
            depth += 1
        elif char == ">":
            depth = max(0, depth - 1)
        elif char == "(" and depth == 0:
            open_index = i
            break
    if open_index < 0:
        return (), None, ""
    close = _matching_close(text, open_index)
    head = text[:open_index].rstrip()
    generics = ()
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
        generics = _generic_param_names(head[k + 1 : -1])
        head = head[:k].rstrip()
    tokens = _split_top_level(head, " ")
    words = [t for t in tokens[:-1] if t not in METHOD_FLAGS and not t.startswith("pinvokeimpl")]
    return generics, text[open_index + 1 : close], " ".join(words)


def _field_declaration(text):
    """(name, flag set, type text) of a `.field` declaration line (joined)."""
    body = text.strip()[len(".field") :]
    cut = _top_level_find(body, "=")
    if cut >= 0:
        body = body[:cut]
    body = re.sub(r"\bat\s+\S+\s*$", "", body.strip())
    body = re.sub(r"^\[\d+\]\s*", "", body.strip())  # explicit layout offset
    tokens = _split_top_level(body, " ")
    if not tokens:
        return None, set(), ""
    name = tokens[-1].strip("'")
    flags = set()
    type_parts = []
    for token in tokens[:-1]:
        if not type_parts and token in FIELD_FLAGS:
            flags.add(token)
        elif not type_parts and token.startswith("marshal("):
            continue
        else:
            type_parts.append(token)
    return name, flags, " ".join(type_parts)


FIELD_FLAGS = frozenset(
    """public private assembly family famandassem famorassem privatescope static initonly
    literal notserialized specialname rtspecialname""".split()
)


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
    "ops": [(opcode, use or None), ...]} plus the member facts ilfacts.py
    reads: "generics" (generic parameter names), "params" (normalized
    parameter types), "returnType", "attributes", "branches",
    "switchTargets" (distinct targets per switch, summed), "switchArms",
    "catches", "refs" [(opcode, assembly or None, exact declaring path, name,
    generic arity, params or None for a field, normalized return type)] and
    "external" {"body", "locals", "signature": {"assembly|type"}}.
    The facts layer also gets "fields" [field record], "typeInfo" {path:
    {"header", "generics", "attributes", "events"}} and "assembly" (the
    assembly's own name).
    """
    uses = defaultdict(list)
    literals = defaultdict(set)
    type_paths = []
    generic_paths = []
    methods = []
    fields = []
    type_info = {}
    assembly_name = None
    class_stack = []  # (brace depth inside the class, canonical path)
    depth = 0
    pending_class = None
    pending_method = None
    method = None
    method_depth = None
    directive = None  # (kind, text) of a multi-line directive or instruction
    # Where the next `.custom` belongs: ("type", info), ("field", record),
    # ("method", method) or None (a parameter, property or event attribute).
    custom_slot = None
    directive_slot = None
    lines = text.splitlines()

    def current_path():
        return class_stack[-1][1] if class_stack else None

    def attribute_name(text):
        parsed = parse_member_signature(text.strip()[len(".custom") :])
        if parsed is None:
            return None
        assembly, path, _generic = split_declaring(parsed[0])
        return path if assembly is None else assembly + "|" + path

    def record_decl_facts(opcode, operand, slot):
        nonlocal custom_slot
        if opcode == "custom":
            if slot is not None:
                name = attribute_name(operand)
                if name is not None:
                    slot[1]["attributes"].append(name)
            return
        if opcode == "field" and method is None and class_stack:
            name, flags, type_text = _field_declaration(operand)
            if name is None:
                return
            record = {
                "type": current_path(),
                "name": name,
                "flags": flags,
                "fieldType": normalize_type_text(type_text),
                "attributes": [],
            }
            fields.append(record)
            custom_slot = ("field", record)
            return
        if opcode == "event" and method is None and class_stack:
            event_name = _split_top_level(operand.strip()[len(".event") :], " ")
            if event_name:
                type_info[current_path()]["events"].add(event_name[-1].strip("'"))

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
            if method is not None:
                method["external"]["locals"] |= external_types(operand)
            return
        if kind == "decl":
            record_decl_facts(opcode, operand, directive_slot)
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
        if method is not None:
            if CONDITIONAL_BRANCH_RE.match(opcode):
                method["branches"] += 1
            elif opcode == "switch":
                targets = SWITCH_TARGET_RE.findall(operand)
                method["switchTargets"] += len(set(targets))
                method["switchArms"] += len(targets)
            if opcode != "ldstr":
                method["external"]["body"] |= external_types(operand)
        if opcode in MEMBER_OPCODES or (opcode == "ldtoken" and "::" in operand):
            parsed = parse_member_signature(operand)
            if parsed is not None:
                declaring, name, generic_args, arity, params, ret = parsed
                if method is not None:
                    assembly, exact, _generic = split_declaring(declaring)
                    generic_arity = len(_split_top_level(generic_args, ",")) if generic_args else 0
                    method["refs"].append((
                        opcode,
                        assembly,
                        exact,
                        name,
                        generic_arity,
                        normalize_params(params),
                        normalize_type_text(re.sub(r"^\s*(?:instance\s+)?(?:explicit\s+)?", "", ret))
                        if name in ("op_Implicit", "op_Explicit") else None,
                    ))
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
        if directive is not None and directive[1] == "switch" and SWITCH_CONTINUATION_RE.match(stripped):
            # ikdasm prints each switch target on its own `IL_xxxx,` line.
            directive = (directive[0], directive[1], directive[2] + " " + stripped)
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
                parent_generics = type_info[parent]["generics"] if parent in type_info else ()
                class_stack.append((depth, path))
                type_paths.append(path)
                if "`" in name:
                    generic_paths.append(path)
                info = type_info.setdefault(path, {
                    "header": pending_class,
                    "generics": _class_generics(pending_class, name) or parent_generics,
                    "attributes": [],
                    "events": set(),
                })
                custom_slot = ("type", info)
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
                generics, params_text, return_text = _method_header(pending_method)
                type_generics = type_info[path]["generics"] if path in type_info else ()
                method = {
                    "type": path,
                    "owner": source_owner(path) if path else None,
                    "name": name,
                    "arity": arity,
                    "flags": flags,
                    "ops": [],
                    "generics": generics,
                    "params": normalize_params(params_text, type_generics, generics, named=True),
                    "returnType": normalize_type_text(return_text, type_generics, generics),
                    "attributes": [],
                    "branches": 0,
                    "switchTargets": 0,
                    "switchArms": 0,
                    "catches": 0,
                    "refs": [],
                    "external": {
                        "body": set(),
                        "locals": set(),
                        "signature": external_types(pending_method),
                    },
                }
                custom_slot = ("method", method)
                methods.append(method)
                method_depth = depth
                pending_method = None
            continue
        if stripped == "}":
            if method is not None and method_depth == depth:
                method = None
                method_depth = None
                custom_slot = None
            if class_stack and class_stack[-1][0] == depth:
                class_stack.pop()
                custom_slot = None
            depth = max(0, depth - 1)
            continue
        if stripped.startswith(".class"):
            pending_class = stripped
            continue
        if stripped.startswith(".method"):
            pending_method = stripped
            custom_slot = None
            continue
        if stripped.startswith(".locals"):
            directive = ("locals", None, stripped)
            continue
        if stripped.startswith((".field", ".custom", ".override", ".property", ".event", ".param type", ".interfaceimpl")):
            directive = ("decl", stripped.split()[0].lstrip("."), stripped)
            if directive[1] == "custom":
                directive_slot = custom_slot
            elif directive[1] in ("property", "event", "param", "interfaceimpl"):
                custom_slot = None
            continue
        if stripped.startswith(".param"):
            custom_slot = None  # `.param [n]`: the attributes after it are the parameter's
            continue
        if stripped.startswith(".assembly") and not stripped.startswith(".assembly extern"):
            assembly_name = stripped.split()[-1].strip("'")
            continue
        # .get / .set / .addon / .removeon name the type's own accessors;
        # .maxstack, .entrypoint, .try, catch handlers, labels carry no use
        # except a catch clause's exception type.
        if stripped.startswith(("catch ", "filter")):
            add_type_uses(stripped, "catch")
            if method is not None:
                method["catches"] += 1
    flush_instruction()
    return {
        "uses": dict(uses),
        "methods": methods,
        "literals": {owner: sorted(names) for owner, names in literals.items()},
        "types": type_paths,
        "genericTypes": generic_paths,
        "fields": fields,
        "typeInfo": type_info,
        "assembly": assembly_name,
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


_PARSED_CACHE = {}


def load_parsed(path):
    """parse_il(load_il(path)), disassembled and parsed once per process.

    The semantic graph and the member facts read the same parse, so a test
    run that needs both (CI's compiled-IL step) pays for one ikdasm and one
    parse. Callers must not mutate the result.
    """
    path = Path(path).resolve()
    stat = path.stat()
    key = (str(path), stat.st_mtime_ns, stat.st_size)
    if key not in _PARSED_CACHE:
        _PARSED_CACHE.clear()
        _PARSED_CACHE[key] = parse_il(load_il(path))
    return _PARSED_CACHE[key]
