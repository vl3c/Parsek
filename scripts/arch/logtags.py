#!/usr/bin/env python3
"""The ParsekLog subsystem tags, read from the compiled assembly, with the modules that log them.

Every Parsek log line is `[Parsek][LEVEL][Tag] message` (`ParsekLog.Write`). The
harness's per-lane health (harness/lib/runindex.py) counts lines per tag in a
flight's KSP.log; this module says which modules write each tag, so a count by
tag becomes a count by module ("which lanes exercise module X").

It reads the `ikdasm` dump of Parsek.dll and finds every call to a ParsekLog
emitter (`Info`, `Verbose`, `Warn`, `Error`, the rate-limited and on-change
variants, and `Write`, whose tag is its second argument). For each call site it
follows the values on the method's evaluation stack (one forward pass per
method; a branch target takes the stack recorded at its branches, two values
that differ merge to unknown, a catch handler starts with the exception) to
find what was loaded for the tag argument:

- `literal`: an `ldstr` (a `const string` is compiled to one), or a local that
  only ever held one literal in that method;
- `static`: an `ldsfld` of a static string field whose type constructor stores
  exactly one literal into it (`static readonly string Tag = "X"`), resolved to
  that literal;
- `parameter`: the caller's own parameter. Such a method is a forwarding helper
  (`void Log(string tag, string msg) => ParsekLog.Info(tag, msg)`); its call
  sites that pass a literal at that position are counted as sites of that tag,
  to a fixed point, so a helper of a helper resolves too;
- anything else (a field, a call result, a value the pass lost) is non-literal
  and listed by method, never guessed.

ParsekLog's own calls (Info forwarding to Write) are the emitters, not sites.
The module of a site is the module `modules.toml` assigns the calling type's
source file (generated closures and state machines credit their source type).

Output (`--write`, the committed `scripts/arch/log_tags.json`, schema
`parsek-log-tags/1`): per tag its call sites, the modules with their site
counts and the levels; the non-literal sites by kind and method; the
forwarding helpers. Sorted, no paths, no build facts, so the file changes only
when the logging code does. The harness reads that FILE; it never imports this
module. Staleness is report-only on the harness side: a tag in a log that the
map lacks is counted as unmapped, and nothing reds when C# adds a tag.

    python3 scripts/arch/logtags.py Source/Parsek/bin/Debug/Parsek.dll           # summary
    python3 scripts/arch/logtags.py Source/Parsek/bin/Debug/Parsek.dll --write   # refresh the map

Everything except `main` is pure. Plain stdlib.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from collections import Counter, defaultdict
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import archview  # noqa: E402
import ilfacts  # noqa: E402
import ilgraph  # noqa: E402

SCHEMA = "parsek-log-tags/1"
DEFAULT_OUTPUT = Path(__file__).resolve().parent / "log_tags.json"
LOGGER_TYPE = "Parsek.ParsekLog"
# ParsekLog method -> (index of the tag argument, level it writes or None).
# `Write(level, subsystem, message)` takes its level as an argument.
EMITTERS = {
    "Info": (0, "INFO"),
    "Verbose": (0, "VERBOSE"),
    "Warn": (0, "WARN"),
    "Error": (0, "ERROR"),
    "VerboseRateLimited": (0, "VERBOSE"),
    "VerboseOnChange": (0, "VERBOSE"),
    "WarnRateLimited": (0, "WARN"),
    "InfoRateLimited": (0, "INFO"),
    "Write": (1, None),
}
OTHER = ("other",)
# Helper-of-helper depth the forwarding resolution follows at most.
MAX_HELPER_DEPTH = 8
LDSTR_RE = re.compile(r'\bldstr\s+"((?:[^"\\]|\\.)*)"\s*$')
BYTEARRAY_RE = re.compile(r"\bldstr\s+bytearray\s*\(([0-9A-Fa-f\s]*)\)")
LABEL_RE = re.compile(r"^\s*(IL_[0-9a-fA-F]+):")
TARGET_RE = re.compile(r"\bIL_[0-9a-fA-F]+\b")
TWO_OPERAND_BRANCH_RE = re.compile(r"^(?:beq|bge|bgt|ble|blt|bne)(?:\.un)?(?:\.s)?$")
ONE_OPERAND_BRANCH_RE = re.compile(r"^(?:brtrue|brfalse|brnull|brinst|brzero)(?:\.s)?$")
UNCONDITIONAL_RE = re.compile(r"^(?:br|leave)(?:\.s)?$")
ESCAPES = {"n": "\n", "t": "\t", "r": "\r", '"': '"', "\\": "\\", "0": "\0", "'": "'"}


def _unescape(text):
    out = []
    i = 0
    while i < len(text):
        char = text[i]
        if char == "\\" and i + 1 < len(text):
            out.append(ESCAPES.get(text[i + 1], text[i + 1]))
            i += 2
            continue
        out.append(char)
        i += 1
    return "".join(out)


def _bytearray_text(hex_text):
    """A `bytearray (..)` ldstr operand (UTF-16LE bytes) as text, or None."""
    try:
        return bytes(int(b, 16) for b in hex_text.split()).decode("utf-16-le")
    except (ValueError, UnicodeDecodeError):
        return None


def _merge(a, b):
    """Two stacks met at one label: equal values stay, others become unknown."""
    if a is None:
        return list(b)
    if b is None:
        return list(a)
    size = min(len(a), len(b))
    merged = []
    for i in range(size):
        x = a[len(a) - size + i]
        y = b[len(b) - size + i]
        merged.append(x if x == y else OTHER)
    return merged


class _Method:
    def __init__(self, type_path, name, arity, params, instance, is_cctor):
        self.type_path = type_path
        self.owner = ilgraph.source_owner(type_path) or type_path
        self.name = name
        self.arity = arity
        self.params = {p: i for i, p in enumerate(params) if p}
        self.instance = instance
        self.is_cctor = is_cctor
        self.instructions = []  # (label, opcode, operand, literal text or None)


def _iter_methods(text):
    """Every method body of an ikdasm dump, its instructions in order."""
    class_stack = []
    depth = 0
    pending_class = None
    pending_method = None
    method = None
    method_depth = None
    directive = None  # [label, opcode, operand, literal, raw lines]

    def flush():
        nonlocal directive
        if directive is None:
            return
        label, opcode, operand, literal, raws = directive
        directive = None
        if method is None:
            return
        if opcode == "ldstr" and literal is None:
            joined = " ".join(raws)
            match = BYTEARRAY_RE.search(ilgraph.strip_strings_and_comments(joined)) or BYTEARRAY_RE.search(joined)
            if match:
                literal = _bytearray_text(match.group(1))
        method.instructions.append((label, opcode, operand, literal))

    for raw in text.splitlines():
        line = ilgraph.strip_strings_and_comments(raw)
        stripped = line.strip()
        if not stripped:
            continue
        instruction = ilgraph.INSTRUCTION_RE.match(line)
        if instruction:
            flush()
            label = LABEL_RE.match(line).group(1)
            opcode, operand = instruction.group(1), instruction.group(2)
            literal = None
            if opcode == "ldstr":
                match = LDSTR_RE.search(raw)
                if match:
                    literal = _unescape(match.group(1))
            directive = [label, opcode, operand, literal, [raw]]
            continue
        if directive is not None and not (stripped.startswith(".") or stripped in ("{", "}")) \
                and not stripped.startswith(("catch ", "finally", "fault", "filter", "IL_")):
            directive[2] = directive[2] + " " + stripped
            directive[4].append(raw)
            continue
        if directive is not None and directive[1] == "switch" and ilgraph.SWITCH_CONTINUATION_RE.match(stripped):
            directive[2] = directive[2] + " " + stripped
            continue
        flush()
        if pending_class is not None and stripped != "{":
            pending_class += " " + stripped
            continue
        if pending_method is not None and stripped != "{":
            pending_method += " " + stripped
            continue
        if stripped == "{":
            depth += 1
            if pending_class is not None:
                name = ilgraph._class_name(pending_class.split(" extends ")[0].split(" implements ")[0])
                parent = class_stack[-1][1] if class_stack else None
                path = ilgraph.canonical_path(name) if parent is None else parent + "/" + ilgraph.canonical_path(name)
                class_stack.append((depth, path))
                pending_class = None
            elif pending_method is not None:
                name, arity, flags = ilgraph._method_signature(pending_method)
                _generics, params_text, _ret = ilgraph._method_header(pending_method)
                path = class_stack[-1][1] if class_stack else None
                if path is not None and name:
                    method = _Method(path, name, arity, ilgraph._parameter_names(params_text),
                                     "static" not in flags, name == ".cctor")
                    method_depth = depth
                pending_method = None
            elif method is not None:
                method.instructions.append((None, "{", "", None))
            continue
        if stripped == "}":
            if method is not None and method_depth == depth:
                yield method
                method = None
                method_depth = None
            elif method is not None:
                method.instructions.append((None, "}", "", None))
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
        if method is not None:
            if stripped.startswith(".try"):
                method.instructions.append((None, ".try", "", None))
            elif stripped.startswith(("catch ", "filter")):
                method.instructions.append((None, ".catch", "", None))
            elif stripped.startswith(("finally", "fault")):
                method.instructions.append((None, ".finally", "", None))
    flush()


_SHAPES = {}
_FIELD_KEYS = {}


def _call_shape(operand):
    """(assembly, declaring path, name, arity, instance, returns) of a call operand, or None."""
    if operand in _SHAPES:
        return _SHAPES[operand]
    shape = _call_shape_uncached(operand)
    _SHAPES[operand] = shape
    return shape


def _field_key(operand):
    """`Path::name` of an in-assembly field operand, else None."""
    if operand in _FIELD_KEYS:
        return _FIELD_KEYS[operand]
    key = None
    parsed = ilgraph.parse_member_signature(operand)
    if parsed is not None:
        assembly, exact, _generic = ilgraph.split_declaring(parsed[0])
        if assembly is None:
            key = exact + "::" + parsed[1]
    _FIELD_KEYS[operand] = key
    return key


def _call_shape_uncached(operand):
    parsed = ilgraph.parse_member_signature(operand)
    if parsed is None:
        return None
    declaring, name, _generic_args, arity, params, ret = parsed
    assembly, exact, _generic = ilgraph.split_declaring(declaring)
    words = re.sub(r"^\s*explicit\s+", "", ret.strip()).split()
    instance = bool(words) and words[0] == "instance"
    rest = [w for w in words if w not in ("instance", "explicit")]
    vararg = params is not None and "..." in params
    return assembly, exact, name, None if vararg else arity, instance, not (rest and rest[0] == "void")


def _parameter_index(method, opcode, operand):
    head, _dot, tail = opcode.partition(".")
    if tail.isdigit():
        slot = int(tail)
    else:
        name = (operand or "").strip().strip("'")
        if name.isdigit():
            slot = int(name)
        else:
            return method.params.get(name)
    if method.instance:
        return slot - 1 if slot > 0 else None
    return slot


def simulate(method, on_call):
    """Two forward passes over `method`'s instructions. The first learns what
    each local slot ever holds (flow-insensitive: a slot that holds two values
    in one method reads unknown); the second follows the stack and calls
    `on_call(opcode, shape, args)` at every call whose shape is known, with the
    argument values (deepest first, `this` dropped). Returns {static field key:
    set of values stored} (read for a `.cctor`)."""
    locals_seen = defaultdict(set)
    stores = defaultdict(set)
    for phase in (0, 1):
        _pass(method, phase, locals_seen, stores, on_call)
    return stores


def _pass(method, phase, locals_seen, stores, on_call):
    states = {}
    stack = []
    pending = None

    def pop(n):
        nonlocal stack
        if not n:
            return []
        if len(stack) >= n:
            taken = stack[-n:]
            del stack[-n:]
            return taken
        taken = [OTHER] * (n - len(stack)) + stack
        stack = []
        return taken

    def record(target, state):
        states[target] = _merge(states[target], state) if target in states else list(state)

    for label, opcode, operand, literal in method.instructions:
        if opcode in ("{", "}"):
            continue
        if opcode in (".try", ".finally"):
            pending = []
            continue
        if opcode == ".catch":
            pending = [OTHER]
            continue
        if pending is not None:
            stack = pending
            pending = None
        if label in states:
            recorded = states.pop(label)
            stack = list(recorded) if stack is None else _merge(stack, recorded)
        if stack is None:
            stack = []
        if opcode == "ldstr":
            stack.append(("lit", literal) if literal is not None else OTHER)
        elif opcode.startswith("ldarg") and not opcode.startswith("ldarga"):
            index = _parameter_index(method, opcode, operand)
            stack.append(("arg", index) if index is not None else OTHER)
        elif opcode.startswith("ldloc") and not opcode.startswith("ldloca"):
            values = locals_seen.get(ilgraph.local_index(opcode, operand)) if phase == 1 else None
            stack.append(next(iter(values)) if values and len(values) == 1 else OTHER)
        elif opcode.startswith("stloc"):
            value = pop(1)[0]
            if phase == 0:
                locals_seen[ilgraph.local_index(opcode, operand)].add(value)
        elif opcode in ("ldsfld", "stsfld"):
            key = _field_key(operand)
            if opcode == "ldsfld":
                stack.append(("sfld", key) if key is not None else OTHER)
            else:
                value = pop(1)[0]
                if key is not None and phase == 1:
                    stores[key].add(value)
        elif opcode == "dup":
            top = pop(1)[0]
            stack.extend((top, top))
        elif ONE_OPERAND_BRANCH_RE.match(opcode) or TWO_OPERAND_BRANCH_RE.match(opcode):
            pop(2 if TWO_OPERAND_BRANCH_RE.match(opcode) else 1)
            for target in TARGET_RE.findall(operand):
                record(target, stack)
        elif UNCONDITIONAL_RE.match(opcode):
            if opcode.startswith("leave"):
                stack = []
            for target in TARGET_RE.findall(operand):
                record(target, stack)
            stack = None
        elif opcode == "switch":
            pop(1)
            for target in TARGET_RE.findall(operand):
                record(target, stack)
        elif opcode in ("ret", "throw", "rethrow", "endfinally", "endfault", "endfilter", "jmp"):
            stack = None
        elif opcode in ("call", "callvirt", "newobj"):
            shape = _call_shape(operand)
            if shape is None or shape[3] is None:
                stack = []
                continue
            _assembly, _exact, name, arity, instance, returns = shape
            has_this = instance and opcode != "newobj"
            args = pop(arity + (1 if has_this else 0))
            if phase == 1:
                on_call(opcode, shape, args[1:] if has_this else args)
            if opcode == "newobj":
                stack.append(OTHER)
            elif returns:
                stack.append(("call", name))
        else:
            effect = ilgraph.stack_effect(opcode)
            if effect is None or effect == "flow":
                stack = []
                continue
            pops, pushes = effect
            pop(pops)
            stack.extend([OTHER] * pushes)


def _method_key(type_path, name, arity):
    return "%s::%s/%s" % (type_path, name, arity)


def extract(text):
    """(raw call sites, static literals) of an IL dump.

    raw call sites: [(calling method, callee type path, callee name, arity,
    argument values)] for every in-assembly call, an argument value being
    ("lit", s), ("sfld", key), ("arg", index), ("call", name) or unknown;
    `resolve` picks the ParsekLog ones and the forwarding helpers out of them.
    static literals: {field key: s} for a static field every type constructor
    store of which is the one literal s."""
    raw_sites = []
    static_stores = defaultdict(set)
    calls_by_method = []
    for method in _iter_methods(text):
        found = []

        def on_call(opcode, shape, args, method=method, found=found):
            assembly, exact, name, arity, _instance, _returns = shape
            if assembly is not None or opcode == "newobj":
                return
            found.append((exact, name, arity, args))

        stores = simulate(method, on_call)
        if method.is_cctor:
            for key, values in stores.items():
                static_stores[key] |= values
        calls_by_method.append((method, found))
    statics = {}
    for key, values in static_stores.items():
        if len(values) == 1:
            value = next(iter(values))
            if value[0] == "lit":
                statics[key] = value[1]
    for method, found in calls_by_method:
        for exact, name, arity, args in found:
            raw_sites.append((method, exact, name, arity, args))
    return raw_sites, statics


def _value_kind(value, statics):
    if value[0] == "lit":
        return "literal", value[1]
    if value[0] == "sfld":
        if value[1] in statics:
            return "static", statics[value[1]]
        return "field", None
    if value[0] == "arg":
        return "parameter", value[1]
    if value[0] == "call":
        return "call", None
    return "other", None


def resolve(raw_sites, statics):
    """Tag sites through ParsekLog and its forwarding helpers, to a fixed point.

    Returns (sites, nonliteral, forwarders): sites [(owner, tag, level, kind,
    via)], nonliteral [(owner::method, kind, via)], forwarders {key: (param
    index, level, emitter)}."""
    forwarders = {}

    def target_of(exact, name, arity):
        if exact == LOGGER_TYPE and name in EMITTERS:
            index, level = EMITTERS[name]
            return index, level, name
        return forwarders.get(_method_key(exact, name, arity))

    # Each pass rebuilds the helper table from the previous one, so a helper
    # that forwards to two emitters (Info and Warn) gets one merged entry
    # instead of flipping between them; the table only grows by depth.
    for _round in range(MAX_HELPER_DEPTH):
        found = defaultdict(set)
        for method, exact, name, arity, args in raw_sites:
            if method.type_path == LOGGER_TYPE or method.owner == LOGGER_TYPE:
                continue
            target = target_of(exact, name, arity)
            if target is None or target[0] >= len(args):
                continue
            kind, value = _value_kind(args[target[0]], statics)
            if kind != "parameter":
                continue
            level = target[1]
            if level is None and target[0] == 1 and args and args[0][0] == "lit":
                level = args[0][1]
            found[_method_key(method.type_path, method.name, method.arity)].add((value, level, target[2]))
        rebuilt = {}
        for key, entries in found.items():
            indexes = sorted({e[0] for e in entries})
            levels = sorted({e[1] or "?" for e in entries})
            emitters = sorted({part for e in entries for part in e[2].split("+")})
            rebuilt[key] = (indexes[0], levels[0] if len(levels) == 1 else "mixed",
                            emitters[0] if len(emitters) == 1 else "+".join(emitters))
        if rebuilt == forwarders:
            break
        forwarders = rebuilt
    else:
        raise ValueError("forwarding helpers did not settle in %d rounds" % MAX_HELPER_DEPTH)
    sites = []
    nonliteral = []
    for method, exact, name, arity, args in raw_sites:
        if method.type_path == LOGGER_TYPE or method.owner == LOGGER_TYPE:
            continue
        target = target_of(exact, name, arity)
        if target is None:
            continue
        via = None if exact == LOGGER_TYPE else "%s::%s" % (exact, name)
        level = target[1]
        if level is None and target[0] == 1 and args and args[0][0] == "lit":
            level = args[0][1]
        if target[0] >= len(args):
            nonliteral.append(("%s::%s" % (method.owner, method.name), "other", via))
            continue
        kind, value = _value_kind(args[target[0]], statics)
        if kind in ("literal", "static"):
            sites.append((method.owner, value, level or "?", kind, via))
        elif kind == "parameter" and _method_key(method.type_path, method.name, method.arity) in forwarders:
            continue  # a forwarding helper: its own callers carry the tag
        else:
            nonliteral.append(("%s::%s" % (method.owner, method.name), kind, via))
    return sites, nonliteral, forwarders


def build_map(sites, nonliteral, forwarders, module_of):
    """The committed map: {"schema", "tags", "nonLiteral", "forwarders", "totals"}."""
    tags = {}
    for owner, tag, level, kind, via in sites:
        entry = tags.setdefault(tag, {"sites": 0, "modules": Counter(), "levels": Counter(), "staticSites": 0,
                                      "helperSites": 0})
        entry["sites"] += 1
        entry["modules"][module_of(owner) or "(unplaced)"] += 1
        entry["levels"][level] += 1
        if kind == "static":
            entry["staticSites"] += 1
        if via is not None:
            entry["helperSites"] += 1
    out_tags = {}
    for tag in sorted(tags):
        entry = tags[tag]
        row = {
            "sites": entry["sites"],
            "modules": dict(sorted(entry["modules"].items())),
            "levels": dict(sorted(entry["levels"].items())),
        }
        if entry["staticSites"]:
            row["staticSites"] = entry["staticSites"]
        if entry["helperSites"]:
            row["helperSites"] = entry["helperSites"]
        out_tags[tag] = row
    by_kind = Counter(kind for _m, kind, _v in nonliteral)
    by_method = Counter(m for m, _k, _v in nonliteral)
    return {
        "schema": SCHEMA,
        "generator": "scripts/arch/logtags.py",
        "totals": {
            "tags": len(out_tags),
            "sites": len(sites),
            "nonLiteralSites": len(nonliteral),
            "forwarders": len(forwarders),
        },
        "tags": out_tags,
        "nonLiteral": {
            "byKind": dict(sorted(by_kind.items())),
            "byMethod": dict(sorted(by_method.items())),
        },
        "forwarders": sorted("%s -> %s(arg %d)" % (key.rsplit("/", 1)[0], emitter, index)
                             for key, (index, _level, emitter) in forwarders.items()),
    }


def module_lookup(source_root=None, modules_path=None):
    """A callable: IL type path -> module name (None when unplaced)."""
    rules, _tooling, _forbidden, _allowed = archview.load_rules(modules_path or archview.DEFAULT_MODULES)
    table = ilfacts.source_type_modules(source_root or (archview.REPO_ROOT / archview.DEFAULT_SOURCE), rules)
    return lambda path: ilfacts.module_for(path, table)


def map_from_il(text, module_of):
    raw_sites, statics = extract(text)
    sites, nonliteral, forwarders = resolve(raw_sites, statics)
    return build_map(sites, nonliteral, forwarders, module_of)


def serialize(tag_map):
    return json.dumps(tag_map, indent=1, sort_keys=True) + "\n"


def format_summary(tag_map, top=10):
    totals = tag_map["totals"]
    lines = ["log tags: %d tags, %d call sites with a literal tag, %d non-literal sites, %d forwarding helpers"
             % (totals["tags"], totals["sites"], totals["nonLiteralSites"], totals["forwarders"])]
    ranked = sorted(tag_map["tags"].items(), key=lambda kv: (-kv[1]["sites"], kv[0]))[:top]
    for tag, entry in ranked:
        mods = ", ".join("%s %d" % (m, n) for m, n in sorted(entry["modules"].items(), key=lambda kv: (-kv[1], kv[0])))
        lines.append("  %-28s %5d  %s" % (tag, entry["sites"], mods))
    if tag_map["nonLiteral"]["byKind"]:
        lines.append("  non-literal by kind: %s" % ", ".join(
            "%s %d" % kv for kv in sorted(tag_map["nonLiteral"]["byKind"].items())))
    return "\n".join(lines)


def main(argv=None):
    parser = argparse.ArgumentParser(description="ParsekLog subsystem tags -> modules, from the compiled IL.")
    parser.add_argument("input", help="Parsek.dll (disassembled with ikdasm) or an ikdasm .il dump")
    parser.add_argument("--write", nargs="?", const=str(DEFAULT_OUTPUT), default=None,
                        help="write the map (default %s)" % DEFAULT_OUTPUT.relative_to(archview.REPO_ROOT).as_posix())
    parser.add_argument("--source", default=None, help="module source root (default Source/Parsek)")
    parser.add_argument("--modules", default=None, help="modules.toml (default scripts/arch/modules.toml)")
    parser.add_argument("--top", type=int, default=10)
    args = parser.parse_args(argv)
    tag_map = map_from_il(ilgraph.load_il(args.input), module_lookup(args.source, args.modules))
    print(format_summary(tag_map, args.top))
    if args.write:
        Path(args.write).write_text(serialize(tag_map), encoding="utf-8", newline="\n")
        print("wrote %s" % args.write)
    return 0


if __name__ == "__main__":
    sys.exit(main())
