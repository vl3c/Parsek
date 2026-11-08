#!/usr/bin/env python3
"""Who writes and who reads each static field of one type, read from the compiled assembly.

The input for a static-state map (split rule S4): before a type's state moves, list every
static field, the methods that write it, the methods that only read it, and the call graph
that says which hosts reach them. A source grep cannot do this safely: a bare field name is
also a local, a parameter, or another type's field (`WatchModeController.CurrentUTNow` is
not `GhostMapPresence.CurrentUTNow`). An IL field operand names its declaring type, so the
scan here is exact about WHICH field an instruction touches.

Usage:

    python3 scripts/refactor/static_state_map.py Parsek.dll --type Parsek.GhostMapPresence \\
        --json out.json
    # a second assembly that only USES the type (the xUnit project), field list from the first:
    python3 scripts/refactor/static_state_map.py Parsek.Tests.dll --type Parsek.GhostMapPresence \\
        --fields-from out.json --json tests.json

Either argument may be an ``ikdasm`` dump instead of a DLL (``--il``).

Output JSON:
- ``fields``: name -> {il, initonly, autoProperty} for every static, non-literal field of the
  type (a static auto-property is listed under its property name; its accessors count).
- ``access``: field -> "Type::Method" -> sorted access kinds, where a kind is one of
  ``reassign`` (stsfld, or a call to the auto-property setter), ``addr`` (ldsflda),
  ``mutate:<M>`` (a mutating collection method called ON the loaded value, see MUTATORS),
  ``read:<M>`` (any other method called on it), ``arg-><Type>::<M>`` (passed as an argument;
  read the callee to know whether it mutates), ``aliased(<op>)`` (stored into another field)
  and ``load`` (consumed by anything else: a branch, a comparison, arithmetic).
- ``calls``: "Type::Method" -> sorted callees (``call`` / ``callvirt`` / ``newobj`` /
  ``ldftn`` / ``ldvirtftn`` operands in the root namespace).

Methods are credited to their SOURCE method: a lambda or local function (``<M>b__``,
``<M>g__``), a closure class and an iterator or async state machine (``<M>d__``) fold onto
the method that wrote them, under the nearest source type. Overloads share one key.

How a loaded value is followed: a linear stack simulation per method body, with the stack
restored at each branch target from the branch that reached it and reset at handler
entries; a value stored to a local and loaded again keeps its tag. It is an analysis of
compiler output, not a verifier: a construct it does not model leaves a value untagged
(under-reports a kind), never attributes a field to the wrong method.

Plain stdlib; everything except ``run_ikdasm`` is pure.
"""

import argparse
import json
import re
import subprocess
import sys
from collections import defaultdict

MUTATORS = frozenset({
    "Add", "AddRange", "Remove", "RemoveAt", "RemoveAll", "RemoveWhere", "RemoveRange",
    "Clear", "Insert", "InsertRange", "set_Item", "TryAdd", "UnionWith", "ExceptWith",
    "IntersectWith", "SymmetricExceptWith", "Enqueue", "Dequeue", "TryDequeue", "Push", "Pop",
    "TryPop", "Sort", "Reverse", "TrimExcess", "set_Capacity",
})

INSTR_RE = re.compile(r"^\s*(IL_[0-9a-f]+):\s+(\S+)\s*(.*)$")
CLASS_RE = re.compile(r"^\s*\.class\s+(.*)$")
END_CLASS_RE = re.compile(r"^\s*\}\s*// end of class ")
END_METHOD_RE = re.compile(r"^\s*\}\s*// end of method (.+)::(.+?)\s*$")
FIELD_DECL_RE = re.compile(r"^\s*\.field\s+(.*?)\s+('?[^\s']+'?)\s*(=.*)?$")
MEMBER_RE = re.compile(r"(Parsek\.[A-Za-z0-9_.`]+(?:/'?[^:\s(]+'?)*)::('?[^(\s]+'?)\s*\(")
FIELD_OPERAND_RE = re.compile(r"\s(Parsek\.[A-Za-z0-9_.`]+(?:/'?[^:\s]+'?)*)::('?[^\s]+'?)\s*$")
BACKING_RE = re.compile(r"^<(.+)>k__BackingField$")
BRANCH_RE = re.compile(r"^(br|b(eq|ne|ge|gt|le|lt)|brtrue|brfalse|leave|switch)")
UNCONDITIONAL = frozenset({"ret", "br", "br.s", "leave", "leave.s", "throw", "rethrow",
                           "endfinally", "endfilter"})


def source_method(type_path, name):
    """Fold a compiler-generated member onto (source type, source method)."""
    parts = type_path.split("/")
    outer = [p for p in parts if not p.strip("'").startswith("<")]
    generated = [p.strip("'") for p in parts if p.strip("'").startswith("<")]
    bare = name.strip("'")
    m = re.match(r"^<([^>]+)>[bg]__", bare)
    if m:
        return "/".join(outer), m.group(1)
    for g in generated:
        m = re.match(r"^<([^>]+)>d__", g)
        if m:
            return "/".join(outer), m.group(1)
    return "/".join(outer), bare


def count_params(operand):
    """Top-level parameter count of the member signature in a call operand."""
    start = operand.find("(", max(operand.find("::"), 0))
    if start < 0:
        return 0
    depth = angle = commas = 0
    nonempty = False
    for ch in operand[start + 1:]:
        if ch == "(":
            depth += 1
        elif ch == ")":
            if depth == 0:
                break
            depth -= 1
        elif ch == "<":
            angle += 1
        elif ch == ">":
            angle -= 1
        elif ch == "," and depth == 0 and angle == 0:
            commas += 1
        elif not ch.isspace():
            nonempty = True
    return commas + 1 if nonempty else 0


def stack_effect(op, operand):
    """(pops, pushes) of one instruction; pops None means 'clears the stack'."""
    if op in ("call", "callvirt"):
        instance = operand.lstrip().startswith("instance")
        void = re.match(r"^\s*(instance\s+)?void\s", operand) is not None
        return count_params(operand) + (1 if instance else 0), 0 if void else 1
    if op == "newobj":
        return count_params(operand), 1
    if op in UNCONDITIONAL:
        return None, 0
    if op == "dup":
        return 1, 2
    if (op.startswith("ldarg") or op.startswith("ldloc") or op.startswith("ldc.")
            or op in ("ldnull", "ldstr", "ldsfld", "ldsflda", "ldtoken", "ldftn", "sizeof",
                      "arglist")):
        return 0, 1
    if (op.startswith("stloc") or op.startswith("starg")
            or op in ("pop", "stsfld", "brtrue", "brtrue.s", "brfalse", "brfalse.s", "switch",
                      "initobj")):
        return 1, 0
    if op in ("stfld", "stobj", "cpobj") or op.startswith("stind") \
            or re.match(r"^b(eq|ne|ge|gt|le|lt)", op):
        return 2, 0
    if op.startswith("stelem") or op in ("cpblk", "initblk"):
        return 3, 0
    if (op.startswith("conv") or op.startswith("ldind")
            or op in ("ldfld", "ldflda", "ldlen", "neg", "not", "box", "unbox", "unbox.any",
                      "castclass", "isinst", "ldobj", "ldvirtftn", "newarr", "localloc",
                      "ckfinite", "refanyval", "mkrefany", "refanytype")):
        return 1, 1
    if re.match(r"^(add|sub|mul|div|rem|and|or|xor|shl|shr|ceq|cgt|clt)", op) \
            or op.startswith("ldelem"):
        return 2, 1
    return 0, 0  # nop, prefixes, break


def callee(operand):
    m = MEMBER_RE.search(operand)
    if m:
        return m.group(1), m.group(2).strip("'")
    m = re.search(r"::('?[^(\s]+'?)\s*\(", operand)
    return None, (m.group(1).strip("'") if m else "?")


def _field_operand(operand, target, fields):
    m = FIELD_OPERAND_RE.search(" " + operand)
    if not m or m.group(1) != target:
        return None
    name = m.group(2).strip("'")
    backing = BACKING_RE.match(name)
    name = backing.group(1) if backing else name
    return name if name in fields else None


def _simulate(body, key, target, fields, access, calls):
    stack = []
    local_tags = {}
    snapshots = {}
    unreachable = False
    for entry in body:
        kind = entry[0]
        if kind == "handler":
            stack = [None] if entry[1] else []
            unreachable = False
            continue
        _, label, op, operand = entry
        if label in snapshots:
            stack = list(snapshots[label])
        elif unreachable:
            stack = []
        unreachable = False

        touched = None
        if op in ("ldsfld", "stsfld", "ldsflda"):
            touched = _field_operand(operand, target, fields)
            if touched:
                access[touched][key]
                if op == "stsfld":
                    access[touched][key].add("reassign")
                elif op == "ldsflda":
                    access[touched][key].add("addr")
        if op in ("call", "callvirt", "newobj", "ldftn", "ldvirtftn"):
            ctype, cname = callee(operand)
            if ctype:
                st, sm = source_method(ctype, cname)
                calls[key].add("%s::%s" % (st, sm))
                if ctype == target and cname[:4] in ("get_", "set_") and cname[4:] in fields:
                    access[cname[4:]][key].add(
                        "reassign" if cname.startswith("set_") else "read:get")

        pops, pushes = stack_effect(op, operand)
        if pops is None:
            stack = []
            unreachable = True
            continue
        popped = [stack.pop() if stack else None for _ in range(pops)]
        popped.reverse()
        for index, tag in enumerate(popped):
            if not tag:
                continue
            if op in ("call", "callvirt"):
                ctype, cname = callee(operand)
                receiver = pops != count_params(operand) and index == 0
                if receiver:
                    text = ("mutate:" if cname in MUTATORS else "read:") + cname
                else:
                    text = "arg->%s::%s" % ((ctype or "ext").replace("Parsek.", ""), cname)
            elif op == "newobj":
                ctype, _ = callee(operand)
                text = "arg->new %s" % (ctype or "ext").replace("Parsek.", "")
            elif op.startswith("stloc"):
                continue
            elif op in ("stfld", "stsfld") or op.startswith("stelem"):
                text = "aliased(%s)" % op
            else:
                text = "load"
            access[tag][key].add(text)
        if op.startswith("stloc"):
            local_tags[operand or op.split(".")[-1]] = popped[0] if popped else None
        push_tag = None
        if op == "ldsfld":
            push_tag = touched
        elif op.startswith("ldloc") and not op.startswith("ldloca"):
            push_tag = local_tags.get(operand or op.split(".")[-1])
        if op == "dup":
            stack.extend([popped[0], popped[0]])
        else:
            stack.extend([push_tag] * pushes)
        if BRANCH_RE.match(op):
            for target_label in re.findall(r"IL_[0-9a-f]+", operand):
                snapshots.setdefault(target_label, list(stack))


def analyze(lines, target, preset_fields=None):
    """Return (fields, access, calls) for `target` from ikdasm text lines (pure)."""
    fields = dict(preset_fields or {})
    access = defaultdict(lambda: defaultdict(set))
    calls = defaultdict(set)
    class_stack = []
    body = None
    for raw in lines:
        line = raw.replace("[Parsek]Parsek.", "Parsek.")
        if body is None:
            mc = CLASS_RE.match(line)
            if mc:
                decl = mc.group(1).split()
                name = decl[-1]
                if "nested" in decl and class_stack:
                    name = class_stack[-1] + "/" + name
                class_stack.append(name)
                continue
            if END_CLASS_RE.match(line):
                if class_stack:
                    class_stack.pop()
                continue
            current = class_stack[-1] if class_stack else ""
            if current == target:
                fd = FIELD_DECL_RE.match(line)
                if fd:
                    mods = fd.group(1).split()
                    if "static" in mods and "literal" not in mods:
                        name = fd.group(2).strip("'")
                        backing = BACKING_RE.match(name)
                        if not name.startswith("<") or backing:
                            fields[backing.group(1) if backing else name] = {
                                "il": fd.group(1), "initonly": "initonly" in mods,
                                "autoProperty": bool(backing)}
            if line.lstrip().startswith(".method"):
                body = []
            continue
        em = END_METHOD_RE.match(line)
        if em:
            st, sm = source_method(class_stack[-1] if class_stack else "", em.group(2))
            _simulate(body, "%s::%s" % (st, sm), target, fields, access, calls)
            body = None
            continue
        stripped = line.strip()
        if stripped.startswith("catch ") or stripped.startswith("filter"):
            body.append(("handler", True))
            continue
        if stripped.startswith("finally") or stripped.startswith("fault"):
            body.append(("handler", False))
            continue
        mi = INSTR_RE.match(line)
        if mi:
            body.append(("instr", mi.group(1), mi.group(2), mi.group(3)))
    return fields, access, calls


def run_ikdasm(path):
    res = subprocess.run(["ikdasm", path], capture_output=True, text=True, errors="replace")
    if res.returncode != 0:
        raise SystemExit("static_state_map: ikdasm failed on %s" % path)
    return res.stdout.splitlines()


def to_json(fields, access, calls):
    return {
        "fields": fields,
        "access": {f: {m: sorted(k) for m, k in sorted(ms.items())}
                   for f, ms in sorted(access.items())},
        "calls": {m: sorted(c) for m, c in sorted(calls.items())},
    }


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("assembly", help="Parsek.dll (or an ikdasm dump with --il)")
    ap.add_argument("--type", required=True, help="declaring type, e.g. Parsek.GhostMapPresence")
    ap.add_argument("--il", action="store_true", help="the argument is an ikdasm dump")
    ap.add_argument("--fields-from", help="JSON of a run over the declaring assembly")
    ap.add_argument("--json", help="write the full result here")
    args = ap.parse_args(argv)
    if args.il:
        with open(args.assembly, encoding="utf-8", errors="replace") as f:
            lines = f.read().splitlines()
    else:
        lines = run_ikdasm(args.assembly)
    preset = None
    if args.fields_from:
        with open(args.fields_from) as f:
            preset = json.load(f)["fields"]
    fields, access, calls = analyze(lines, args.type, preset)
    result = to_json(fields, access, calls)
    if args.json:
        with open(args.json, "w") as f:
            json.dump(result, f, indent=1, sort_keys=True)
    for name in sorted(fields, key=str.lower):
        print("%-50s methods=%d" % (name, len(result["access"].get(name, {}))))
    return 0


if __name__ == "__main__":
    sys.exit(main())
