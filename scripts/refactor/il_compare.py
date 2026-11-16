#!/usr/bin/env python3
"""Compare two builds of Parsek.dll type by type, field by field and method by method.

The proof a pure move needs: a partial-file split (Phase A) must leave every type, field,
property and method body identical; a member move into a new type (Phase B) must leave
every body identical once the move itself is folded back.

Usage:

    python3 scripts/refactor/il_compare.py BEFORE.dll AFTER.dll
    python3 scripts/refactor/il_compare.py BEFORE.dll AFTER.dll \\
        --fold Parsek.ParsekFlight=Parsek.ParsekFlightGhostPositioner --host-field host

The disassembly comes from ``ikdasm`` (mono) unless ``--il`` names pre-made dumps.

What is compared, per type (nested types keyed by their full nesting path):
- the type header (kind, visibility, base type, interfaces, attributes on the header line);
- the multiset of ``.field`` declarations;
- the multiset of ``.property`` / ``.event`` declarations;
- every method, keyed by its normalized signature, by a hash of its normalized body.
Member and type ORDER is not compared: a partial-file split changes declaration order.

Normalization, always on:
- compiler-generated ordinals that follow declaration order (``<>c__DisplayClass12_0``,
  ``<M>b__12_0``, ``<>9__12_0``, ``<M>d__12``, local functions, ``CS$<>8__locals1``,
  ``<>u__1``) are blanked;
- IL offsets (``IL_0000:``), the ``// Code size`` comment and ``// end of method`` trailers
  are dropped, a branch target names its instruction's ordinal instead of its offset, a
  short branch (``br.s``) reads as its long form, and runs of whitespace are collapsed;
- the blob of an iterator / async state machine attribute is dropped, because it spells the
  generated class name with its ordinal (every other attribute blob is compared);
- the RVA of a mapped data field (``at I_...``) is dropped: the field's name is the SHA-256 of
  its bytes.

``--fold OLD=NEW`` (Phase B, repeatable) folds a member move from type OLD into type NEW
back onto OLD before comparing: every token ``NEW::`` reads ``OLD::``, every method and
field declared in NEW is keyed under OLD, ``ldarg.0`` + ``ldfld class OLD NEW::<host>`` (a
moved body reaching the host) reads ``ldarg.0``, and ``ldarg.0`` + ``call instance class NEW
OLD::get_*`` (a host body reaching the service through its lazy accessor) reads ``ldarg.0``, and
a ``callvirt instance`` on OLD or NEW reads ``call`` (``this.M()`` compiles to call, ``host.M()``
and ``service.M()`` to callvirt).
Under a fold the two hops are the identity wherever they appear (a lambda's display class or a
host static reaching the service through a local, too), and every other NEW token, nested
paths included, reads OLD; NEW's own header is printed, not compared. Both builds are folded,
so a later step of a series of moves into one NEW (the before build already holds NEW) folds
the same way as the first. A member or nested type
under OLD whose declaration differs only in ``private`` -> ``assembly`` (internal) is listed as
widened, not as a difference; its body is still compared. ``--show TEXT`` prints the folded
body diff of each differing method whose signature holds TEXT.
Whatever still differs after the fold is listed: the forwarders, the accessor, the host field
and constructor. A PR lists each of those by name.

``--alias-field TYPE::NEW=OLD`` (repeatable, applied after the folds and to the AFTER build
only) folds a renamed reference field: a service that reached state through ``host.Lane.x``
and now reaches it through its own ``lane`` field. In the after build every read
``ldfld class X TYPE::NEW`` reads ``ldfld class X TYPE::OLD``; ``X`` is kept, so a read of a
field whose (folded) type is not OLD's still differs. Nothing else is aliased: a store
(``stfld``), an address (``ldflda``), a field of a nested or any other type, and any other
name stay as they are, so the constructor that stores NEW and NEW's own ``.field``
declaration are the expected leftovers. Each alias is also checked against the declarations
(kind ``alias-field``, a difference): OLD declared on TYPE before, NEW declared on TYPE after
and not before, and NEW's folded field type equal to OLD's. TYPE is named as in the after
build; a folded NEW type reads as its OLD type. Aliases must be a pure renaming: on one TYPE no
two aliases share a NEW or an OLD, and no name is both a NEW and an OLD (aliases apply in
order, so a chain would fold twice); a set that breaks this is refused (exit 2).

A fold merges OLD's and NEW's members under one key, so it may only merge what IS one thing.
Before folding, each build is checked (kind ``fold-collision``, a difference) for:
- a field name (instance or static, an auto-property or field-like event backing field
  included) declared on both OLD and NEW: two storages that would read as one; a ``literal``
  constant has no storage (its reads are inlined) and is skipped;
- a method signature on both OLD and NEW whose folded bodies differ, unless exactly one of the
  two is a pass-through forwarder to the other: a call moved from one to the other would read
  the same. The forwarder shape is the one every seam lift left behind (``host.Seam.M(args)``,
  ``Seam.M(args)`` for a static): folded, one basic block of ``ldarg.0`` (instance), every
  parameter loaded once in order, one ``call`` of the same folded signature (name, parameter
  types, return type) and ``ret`` (``nop`` allowed; any branch, local, other call or extra
  instruction fails it), and unfolded that call names the OTHER type, not its own. Anything
  else is reported; a person then looks at it. Constructors are skipped (never called by name
  across the two);
- a nested type of the same, not compiler-generated, name on both OLD and NEW.

Exit code 0 when nothing differs, 1 otherwise, 2 on a usage or tool error.
Plain stdlib.
"""

import argparse
import collections
import difflib
import hashlib
import re
import subprocess
import sys

ORDINALS = [
    (re.compile(r"<>c__DisplayClass\d+_\d+"), "<>c__DisplayClassN"),
    (re.compile(r"b__\d+_\d+"), "b__N"),
    (re.compile(r"<>9__\d+_\d+"), "<>9__N"),
    (re.compile(r"d__\d+"), "d__N"),
    (re.compile(r"g__(\w+)\|\d+_\d+"), r"g__\1|N"),
    (re.compile(r"CS\$<>8__locals\d+"), "CS$<>8__localsN"),
    (re.compile(r"<>u__\d+"), "<>u__N"),
    (re.compile(r"<>\d+__"), "<>N__"),
    (re.compile(r"'<\d+>__"), "'<N>__"),
]
BODY_ONLY = [
    (re.compile(r"IL_[0-9a-f]{4}:\s*"), ""),
    (re.compile(r"\} // end of method .*"), "}"),
]
_HEX_ROW = re.compile(r"^\s*(?:[0-9A-F]{2} )+\)?\s*(?://.*)?$")


def _drop_state_machine_blobs(text):
    """An iterator / async state machine attribute spells the generated class name, ordinal
    included, in its blob, so its blob is replaced; every other attribute blob is kept."""
    out = []
    skipping = False
    for line in text.split("\n"):
        if skipping:
            if _HEX_ROW.match(line):
                continue
            skipping = False
        s = line.strip()
        if s.startswith(".custom") and "StateMachineAttribute::.ctor" in s and "= (" in s:
            out.append(s.split("= (", 1)[0] + "= (state machine type)")
            skipping = ")" not in s.split("= (", 1)[1].split("//", 1)[0]
            continue
        out.append(line)
    return "\n".join(out)


_STRING = re.compile(r'"(?:[^"\\]|\\.)*"')


_FIELD_RVA = re.compile(r" at I_[0-9A-Fa-f]+$")


def brace_delta(line):
    """Net '{' minus '}' on one IL line, ignoring string literals and comments."""
    s = _STRING.sub('""', line)
    cut = s.find("//")
    if cut >= 0:
        s = s[:cut]
    return s.count("{") - s.count("}")


def normalize_name(text):
    for rx, rep in ORDINALS:
        text = rx.sub(rep, text)
    return text


_LABEL_DEF = re.compile(r"^\s*(IL_[0-9a-f]{4}):")
_LABEL_REF = re.compile(r"\bIL_[0-9a-f]{4}\b")
_CODE_SIZE = re.compile(r"^\s*// Code size\b.*$", re.M)
# A branch's short form (`br.s`) and long form (`br`) mean the same; the compiler picks the
# long one once a body grows past a signed byte of offset, which a fold hop can cause.
_SHORT_BRANCH = re.compile(
    r"^((?:IL_[0-9a-f]{4}:\s*)?(?:br|brfalse|brtrue|beq|bge|bgt|ble|blt|bne\.un|bge\.un|bgt\.un"
    r"|ble\.un|blt\.un|leave))\.s(?=\s)", re.M)


def _ordinal_labels(text):
    """Branch targets name the target's instruction ordinal instead of its byte offset, so
    dropping a fold hop (which shifts every later offset) leaves the branches equal."""
    ordinal = {}
    for line in text.split("\n"):
        m = _LABEL_DEF.match(line)
        if m:
            ordinal[m.group(1)] = "L%d" % len(ordinal)
    out = []
    for line in text.split("\n"):
        m = _LABEL_DEF.match(line)
        head, rest = (line[:m.end()], line[m.end():]) if m else ("", line)
        out.append(head + _LABEL_REF.sub(lambda r: ordinal.get(r.group(0), r.group(0)), rest))
    return "\n".join(out)


def normalize_body(text):
    text = _drop_state_machine_blobs(text)
    text = _CODE_SIZE.sub("", text)
    text = _SHORT_BRANCH.sub(r"\1", "\n".join(l.strip() for l in text.split("\n")))
    text = _ordinal_labels(text)
    for rx, rep in BODY_ONLY:
        text = rx.sub(rep, text)
    text = normalize_name(text)
    # Operand padding follows the opcode's width (callvirt -> call under a fold).
    return "\n".join(" ".join(l.split()) for l in text.split("\n") if l.strip())


# Non-virtual members a MonoBehaviour host inherits; `host.X()` compiles to callvirt where the
# host's own `X()` compiled to call. Only these are folded: a virtual member's call vs
# callvirt is a real dispatch difference.
HOST_BASE_NONVIRTUAL = (
    "MonoBehaviour::StartCoroutine", "MonoBehaviour::StopCoroutine", "MonoBehaviour::StopAllCoroutines",
    "Component::get_gameObject", "Component::get_transform",
    "Behaviour::get_enabled", "Behaviour::set_enabled",
)


def apply_folds(text, folds, host_field):
    """Rewrite NEW-type tokens back to OLD and drop the host hop instructions."""
    for old, new in folds:
        o, n = re.escape(old), re.escape(new)
        # Under the fold the service IS the host, so the two hops between them are the
        # identity wherever they occur (a moved body, a lambda's display class, a host
        # static reaching the service of a local `instance`):
        # the host field load `ldfld class OLD NEW::host` ... A compound assignment
        # through it (`host.count++`) loads the host once and `dup`s it where the
        # original (`this.count++`) loaded `this` twice, so `ldarg.0; ldfld host; dup`
        # reads `ldarg.0; ldarg.0` ...
        il = r"\n(\s*(?:IL_[0-9a-f]{4}:\s*)?)"
        text = re.sub(il + r"ldarg\.0[ \t]*" + il + r"ldfld\s+class\s+%s\s+%s::%s[ \t]*" % (o, n, re.escape(host_field))
                      + il + r"dup(?=[ \t]*\n)", r"\n\1ldarg.0\n\3ldarg.0", text)
        text = re.sub(r"\n\s*(?:IL_[0-9a-f]{4}:\s*)?ldfld\s+class\s+%s\s+%s::%s[ \t]*(?=\n)"
                      % (o, n, re.escape(host_field)), "", text)
        # ... and the lazy accessor `call(virt) instance class NEW OLD::get_X()`.
        text = re.sub(r"\n\s*(?:IL_[0-9a-f]{4}:\s*)?call(?:virt)?\s+instance\s+class\s+%s\s+%s::get_\w+\(\)[ \t]*(?=\n)"
                      % (n, o), "", text)
        # this.M() compiles to call, service.M() / host.M() to callvirt. The same holds for
        # the host's non-virtual Unity base members (host.StartCoroutine(...)).
        text = re.sub(r"callvirt(\s+instance\s+[^\n]*?[\s\]]UnityEngine\.(?:%s)\()"
                      % "|".join(re.escape(m) for m in HOST_BASE_NONVIRTUAL), r"call\1", text)
        text = re.sub(r"callvirt(\s+instance\s+[^\n]*?\s(?:%s|%s)(?:/[^\s:]+)?::)" % (o, n), r"call\1", text)
        # Every remaining NEW token (member owner, field / local / parameter type, nested
        # display class path) reads OLD. NEW's own `.class` line keeps its name, so
        # fold_types can tell its header from OLD's.
        token = re.compile(r"(?<![\w.])%s(?![\w])" % n)
        text = "\n".join(l if l.lstrip().startswith(".class") else token.sub(old, l)
                         for l in text.split("\n"))
    return text


def fold_type_name(name, folds):
    """The name a type path reads under the folds (a NEW type, or a type nested in it, reads OLD)."""
    for old, new in folds:
        if name == new or name.startswith(new + "/"):
            return old + name[len(new):]
    return name


def parse_alias(spec):
    """``TYPE::NEW=OLD`` -> (TYPE, NEW, OLD), or None when malformed."""
    m = re.match(r"^([^\s:=]+)::([A-Za-z_][\w]*)=([A-Za-z_][\w]*)$", spec)
    if not m or m.group(2) == m.group(3):
        return None
    return m.group(1), m.group(2), m.group(3)


def apply_aliases(text, aliases, folds):
    """Rewrite each read ``ldfld class X TYPE::NEW`` to ``ldfld class X TYPE::OLD``.
    Returns (text, reads rewritten per alias)."""
    counts = []
    for typ, new, old in aliases:
        t = re.escape(fold_type_name(typ, folds))
        rx = re.compile(r"^(\s*(?:IL_[0-9a-f]{4}:\s*)?ldfld\s+class\s+\S.*?\s%s::)%s([ \t]*)$"
                        % (t, re.escape(new)), re.M)
        text, n = rx.subn(lambda m: m.group(1) + old + m.group(2), text)
        counts.append(n)
    return text, counts


_FIELD_MODIFIERS = frozenset((
    "public", "private", "assembly", "family", "famandassem", "famorassem", "privatescope",
    "static", "initonly", "literal", "notserialized", "specialname", "rtspecialname"))


def _field_decls(info, name):
    """The (folded) field types of every declaration of `name` in a TypeInfo."""
    found = []
    for decl in (info.fields if info else ()):
        tokens = decl.split()
        if len(tokens) < 3 or tokens[-1].strip("'") != name:
            continue
        rest = tokens[1:-1]
        while rest and rest[0] in _FIELD_MODIFIERS:
            rest = rest[1:]
        found.append(" ".join(rest))
    return found


def alias_conflicts(aliases, folds):
    """Messages for an alias set that is not a pure renaming per type (see the docstring)."""
    out = []
    by_type = collections.defaultdict(list)
    for typ, new, old in aliases:
        by_type[fold_type_name(typ, folds)].append((new, old))
    for path, pairs in sorted(by_type.items()):
        news = collections.Counter(n for n, _ in pairs)
        olds = collections.Counter(o for _, o in pairs)
        for name in sorted(n for n, c in news.items() if c > 1):
            out.append("%s::%s is aliased more than once" % (path, name))
        for name in sorted(o for o, c in olds.items() if c > 1):
            out.append("%s: two aliases fold onto %s" % (path, name))
        for name in sorted(set(news) & set(olds)):
            out.append("%s::%s is both an alias NEW and an alias OLD" % (path, name))
    return out


def _field_name(decl):
    """The declared name of a `.field` line (None for a literal constant, which has no storage)."""
    head = decl.split(" = ", 1)[0].split()
    if "literal" in head or len(head) < 3:
        return None
    return head[-1].strip("'")


_GENERATED_NESTED = re.compile(r"^'?<")


_METHOD_KEYWORDS = frozenset((
    "public", "private", "assembly", "family", "famorassem", "famandassem", "privatescope",
    "hidebysig", "specialname", "rtspecialname", "virtual", "final", "newslot", "abstract",
    "strict", "instance", "static"))


def _split_params(text):
    """Split a parameter list on the commas outside brackets."""
    parts, depth, cur = [], 0, ""
    for ch in text:
        if ch in "<([":
            depth += 1
        elif ch in ">)]":
            depth -= 1
        if ch == "," and depth == 0:
            parts.append(cur.strip())
            cur = ""
        else:
            cur += ch
    if cur.strip():
        parts.append(cur.strip())
    return parts


def _signature(sig):
    """(static, return type, name, [(param type, param name)]) of a method key, or None."""
    m = re.match(r"^\.method\s+(.*?)\s([^\s(]+)\((.*)\)\s+cil managed$", sig)
    if not m:
        return None
    head = m.group(1).split()
    static = "static" in head
    while head and head[0] in _METHOD_KEYWORDS:
        head = head[1:]
    params = []
    for p in _split_params(m.group(3)):
        p = re.sub(r"^(?:\[(?:opt|out|in)\]\s*)+", "", p)
        typ, _, pname = p.rpartition(" ")
        if not typ:
            return None
        params.append((typ, pname))
    return static, " ".join(head), m.group(2), params


def _instructions(body):
    """The normalized body's instruction lines, a call's wrapped parameter list joined back."""
    out = []
    for line in body.split("\n"):
        if out and out[-1].count("(") > out[-1].count(")"):
            out[-1] += line
        else:
            out.append(line)
    return out


def _forwarder_call(sig, body, owner):
    """True when the folded `body` of method `sig` is exactly a pass-through to `owner`'s method
    of the same folded signature: one basic block, every argument loaded once in order, one call,
    its result returned unchanged. Directives other than .maxstack / .param / .custom (a .locals
    included) and any other instruction but nop fail it."""
    parsed = _signature(sig)
    if parsed is None:
        return False
    static, ret, name, params = parsed
    code = []
    for ins in _instructions(body):
        if ins in ("{", "}", "nop") or ins.startswith((".maxstack", ".param", ".custom")):
            continue
        if ins.startswith("."):
            return False
        code.append(ins)
    expected = [] if static else [{"ldarg.0"}]
    first = 0 if static else 1
    for i, (_, pname) in enumerate(params):
        idx = first + i
        ok = {"ldarg.%d" % idx} if idx <= 3 else set()
        ok |= {"ldarg.s %s" % pname, "ldarg %s" % pname, "ldarg.s %d" % idx, "ldarg %d" % idx}
        expected.append(ok)
    call = "call %s%s %s::%s(%s)" % ("" if static else "instance ", ret, owner, name,
                                     ",".join(t for t, _ in params))
    if code[-2:] != [call, "ret"] or len(code) != len(expected) + 2:
        return False
    return all(ins in ok for ins, ok in zip(code[:-2], expected))


def _fold_tokens(text, folds):
    for old, new in folds:
        text = re.sub(r"(?<![\w.])%s(?![\w])" % re.escape(new), old, text)
    return text


def _raw_body(raw, path, folded_sig, folds):
    """The unfolded body of the method of type `path` whose folded key is `folded_sig`."""
    info = raw.get(path)
    for sig, bodies in (info.methods.items() if info else ()):
        if _fold_tokens(sig, folds) == folded_sig and len(bodies) == 1:
            return BODIES[bodies[0]]
    return None


def fold_collisions(types, folds, label, raw_text=None):
    """(kind, path, detail) for every member a fold would merge although it is two things.
    `types` is a build parsed after apply_folds but before fold_types; `raw_text` is the same
    build unfolded (parsed on demand, to see which type a forwarder really calls)."""
    out = []
    raw = []
    for old, new in folds:
        to, tn = types.get(old), types.get(new)
        if to is None or tn is None:
            continue
        fo = {_field_name(f) for f in to.fields} - {None}
        fn = {_field_name(f) for f in tn.fields} - {None}
        for name in sorted(fo & fn):
            out.append(("fold-collision", old, "%s build: field %s declared on both %s and %s"
                        % (label, name, old, new)))
        for sig in sorted(set(to.methods) & set(tn.methods)):
            if " .ctor(" in sig or " .cctor(" in sig:
                continue
            bo, bn = to.methods[sig], tn.methods[sig]
            if sorted(set(bo)) == sorted(set(bn)):
                continue
            # Allowed only when exactly one side is a pass-through to the other: its folded body
            # has the forwarder shape, and unfolded its one call names the OTHER type.
            forwarders = []
            for path, other, bodies in ((old, new, bo), (new, old, bn)):
                if len(bodies) != 1 or not _forwarder_call(sig, BODIES[bodies[0]], old):
                    continue
                if not raw and raw_text is not None:
                    raw.append(parse_il(raw_text))
                body = _raw_body(raw[0], path, sig, folds) if raw else None
                name = _signature(sig)[2]
                if body is not None and re.search(r"\bcall(?:virt)?\s[^\n]*\s%s::%s\("
                                                  % (re.escape(other), re.escape(name)), body):
                    forwarders.append(path)
            if len(forwarders) == 1:
                continue
            out.append(("fold-collision", old, "%s build: method on both %s and %s, not one pass-through "
                        "forwarder: %s" % (label, old, new, sig)))
        for path in types:
            if path.startswith(new + "/") and "/" not in path[len(new) + 1:]:
                nested = path[len(new) + 1:]
                if not _GENERATED_NESTED.match(nested) and (old + "/" + nested) in types:
                    out.append(("fold-collision", old, "%s build: nested type %s on both %s and %s"
                                % (label, nested, old, new)))
    return out


def alias_checks(a, b, aliases, folds):
    """(kind, path, detail) for every alias whose declarations do not make it a pure rename."""
    out = [("alias-field", "", m) for m in alias_conflicts(aliases, folds)]
    for typ, new, old in aliases:
        path = fold_type_name(typ, folds)
        label = "%s::%s=%s" % (typ, new, old)
        old_before = _field_decls(a.get(path), old)
        new_before = _field_decls(a.get(path), new)
        new_after = _field_decls(b.get(path), new)
        if len(old_before) != 1:
            out.append(("alias-field", path, "%s: %s declared %d times before" % (label, old, len(old_before))))
        if new_before:
            out.append(("alias-field", path, "%s: %s already declared before" % (label, new)))
        if len(new_after) != 1:
            out.append(("alias-field", path, "%s: %s declared %d times after" % (label, new, len(new_after))))
        if len(old_before) == 1 and len(new_after) == 1 and old_before[0] != new_after[0]:
            out.append(("alias-field", path, "%s: type differs (%s before, %s after)"
                        % (label, old_before[0], new_after[0])))
    return out


BODIES = {}  # body hash -> normalized (folded) body text, for --show

TypeInfo = collections.namedtuple("TypeInfo", "header fields props methods")  # header: Counter


def _new_type():
    return TypeInfo(collections.Counter(), collections.Counter(), collections.Counter(),
                    collections.defaultdict(list))


def _type_name(header_line):
    head = header_line.split(" extends ")[0].split(" implements ")[0].strip()
    return head.split()[-1]


def parse_il(text):
    """Map type path -> TypeInfo from an ikdasm dump."""
    types = collections.OrderedDict()
    stack = []  # (path, depth at which the type's body opened)
    depth = 0
    pending = None  # header lines of a .class not yet opened
    method = None
    method_depth = None
    opened = False
    for raw in text.split("\n"):
        s = raw.strip()
        if method is not None:
            method.append(raw)
            delta = brace_delta(s)
            depth += delta
            opened = opened or delta > 0
            if opened and depth == method_depth:
                body = "\n".join(method)
                header = body.split("{", 1)[0]
                sig = normalize_name(" ".join(header.split()))
                path = stack[-1][0] if stack else "<module>"
                if path not in types:
                    types[path] = _new_type()
                # The header is the key; the hash covers what follows it, so a widened
                # method (only its header changed) keeps its hash.
                norm = normalize_body("{" + body.split("{", 1)[1])
                digest = hashlib.sha256(norm.encode()).hexdigest()
                BODIES[digest] = norm
                types[path].methods[sig].append(digest)
                method = None
            continue
        if s.startswith(".class"):
            pending = [s]
            continue
        if pending is not None and not s.startswith("{"):
            pending.append(s)
            continue
        if pending is not None and s.startswith("{"):
            header = normalize_name(" ".join(" ".join(pending).split()))
            name = normalize_name(_type_name(" ".join(pending)))
            path = (stack[-1][0] + "/" + name) if stack else name
            # Normalized compiler-generated names collide (several <>c__DisplayClassN in
            # one type), so one path can hold several types: merge them.
            if path not in types:
                types[path] = _new_type()
            types[path].header[header] += 1
            depth += 1
            stack.append((path, depth))
            pending = None
            continue
        if s.startswith(".method"):
            method = [raw]
            method_depth = depth
            delta = brace_delta(s)
            depth += delta
            opened = delta > 0
            continue
        if stack and s.startswith(".field"):
            # A mapped data field's RVA (`at I_0001A2B0`) follows the data layout; its name is
            # the SHA-256 of its bytes, so the name alone pins the content.
            field = _FIELD_RVA.sub("", " ".join(s.split()))
            types[stack[-1][0]].fields[normalize_name(field)] += 1
        elif stack and (s.startswith(".property") or s.startswith(".event")):
            types[stack[-1][0]].props[normalize_name(" ".join(s.split()))] += 1
        depth += brace_delta(s)
        while stack and depth < stack[-1][1]:
            stack.pop()
    return types


SINGLETON_CACHES = ("'<>O'", "'<>c'")


def fold_types(types, folds):
    """Key every member of a NEW type under its OLD type (nested types re-parented)."""
    if not folds:
        return types
    out = collections.OrderedDict()
    for path, info in types.items():
        target = path
        for old, new in folds:
            if path == new or path.startswith(new + "/"):
                target = old + path[len(new):]
        if target not in out:
            out[target] = _new_type()
        dst = out[target]
        if "/" in target:
            # A nested type of NEW (a display class, a lambda cache) keeps its header; NEW's
            # own header is the one thing the fold drops (main prints it). The compiler's
            # per-type singleton caches ('<>O' for method-group delegates, '<>c' for static
            # lambdas) exist once on OLD and once on NEW: folded they are one type, so their
            # header counts once. Every other nested type (display classes, state machines) adds.
            singleton = target.rsplit("/", 1)[1] in SINGLETON_CACHES
            for h, n in info.header.items():
                dst.header[h] = max(dst.header[h], n) if singleton else dst.header[h] + n
        elif target == path:
            dst.header.update(info.header)
            singleton = False
        else:
            singleton = False
        for f, n in info.fields.items():
            # '<>c' keeps its one instance in '<>9'; both sides' copies are the same field.
            dst.fields[f] = max(dst.fields[f], n) if singleton and "'<>9'" in f else dst.fields[f] + n
        dst.props.update(info.props)
        for sig, bodies in info.methods.items():
            if singleton and (" .ctor(" in sig or " .cctor(" in sig):
                # The cache's own constructors: identical on both sides once folded.
                dst.methods[sig].extend(b for b in bodies if b not in dst.methods[sig])
            else:
                dst.methods[sig].extend(bodies)
    return out


_ACCESS = re.compile(r"\b(private|assembly)\b ?")


def _strip_access(sig):
    return _ACCESS.sub("", sig).strip()


def widenings(a, b, paths):
    """(kind, path, before-key, after-key) for every member or nested type under `paths`
    whose declaration differs only in private -> internal (assembly) accessibility."""
    found = []
    for path in sorted(set(a) & set(b)):
        if not any(path == p or path.startswith(p + "/") for p in paths):
            continue
        ta, tb = a[path], b[path]
        for kind, ca, cb in (("type", ta.header, tb.header), ("field", ta.fields, tb.fields),
                             ("method", ta.methods, tb.methods)):
            only_a = [k for k in ca if k not in cb]
            only_b = [k for k in cb if k not in ca]
            by_neutral = {}
            for k in only_b:
                by_neutral.setdefault(_strip_access(k), []).append(k)
            for k in only_a:
                if " private " not in " %s " % k.replace("(", " ("):
                    continue
                for kb in by_neutral.get(_strip_access(k), []):
                    # A widened method's body is still compared (under its new key).
                    if " assembly " in " %s " % kb.replace("(", " (") and \
                            (kind == "method" or ca[k] == cb[kb]):
                        found.append((kind, path, k, kb))
                        break
            if kind == "method":
                # A private member that moved and left a same-named forwarder behind: the
                # before key is still on OLD (the forwarder) and its widened twin appears
                # only after (the moved original). Paired only when that twin's body is the
                # before body, so the pairing can never hide a change; the forwarder's own
                # body is still reported as a difference.
                for k in ca:
                    if k not in cb or " private " not in " %s " % k.replace("(", " ("):
                        continue
                    for kb in by_neutral.get(_strip_access(k), []):
                        if " assembly " in " %s " % kb.replace("(", " (") and \
                                sorted(ca[k]) == sorted(cb[kb]):
                            found.append(("forwarded", path, k, kb))
                            break
    return found


def compare(a, b, widened=()):
    """Return a list of (kind, type path, detail) differences, leaving out the
    private -> internal widenings listed in `widened` (from widenings())."""
    a = {p: TypeInfo(collections.Counter(t.header), collections.Counter(t.fields),
                     collections.Counter(t.props), collections.defaultdict(list, t.methods))
         for p, t in a.items()}
    for kind, path, ka, kb in widened:
        ta = a[path]
        if kind == "type":
            ta.header[kb] += ta.header.pop(ka)
        elif kind == "field":
            ta.fields[kb] += ta.fields.pop(ka)
        elif kind == "forwarded":
            ta.methods[kb] = list(ta.methods[ka])
        else:
            ta.methods[kb] = ta.methods.pop(ka)
    diffs = []
    for path in sorted(set(a) | set(b)):
        if path not in b:
            diffs.append(("type-only-before", path, ""))
            continue
        if path not in a:
            diffs.append(("type-only-after", path, ""))
            continue
        ta, tb = a[path], b[path]
        if ta.header != tb.header:
            diffs.append(("type-header", path, "%s -> %s" % (sorted(ta.header), sorted(tb.header))))
        for kind, ca, cb in (("field", ta.fields, tb.fields), ("property", ta.props, tb.props)):
            for item in sorted(set(ca) | set(cb)):
                if ca[item] != cb[item]:
                    diffs.append((kind, path, "%s (before %d, after %d)" % (item, ca[item], cb[item])))
        for sig in sorted(set(ta.methods) | set(tb.methods)):
            ma, mb = sorted(ta.methods.get(sig, [])), sorted(tb.methods.get(sig, []))
            if not ma:
                diffs.append(("method-only-after", path, sig))
            elif not mb:
                diffs.append(("method-only-before", path, sig))
            elif ma != mb:
                # A non-private member that moved and left a same-named forwarder behind keys
                # both bodies under one signature: every before body is still there verbatim
                # and exactly one body was added (the forwarder). Any other change to the list
                # is a body difference.
                extra = collections.Counter(mb)
                extra.subtract(collections.Counter(ma))
                kept = min(extra.values()) >= 0 and sum(extra.values()) == 1
                diffs.append(("method-forwarder" if kept else "method-body", path, sig))
    return diffs


def summarize(types):
    return (sum(sum(t.header.values()) for t in types.values()), sum(sum(t.fields.values()) for t in types.values()),
            sum(sum(len(v) for v in t.methods.values()) for t in types.values()))


def disassemble(path):
    try:
        res = subprocess.run(["ikdasm", path], capture_output=True, text=True, errors="replace")
    except OSError as exc:
        raise SystemExit("il_compare: cannot run ikdasm: %s" % exc)
    if res.returncode != 0 or not res.stdout:
        sys.stderr.write(res.stderr)
        raise SystemExit("il_compare: ikdasm failed on %s" % path)
    return res.stdout


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("before")
    ap.add_argument("after")
    ap.add_argument("--il", action="store_true", help="the two arguments are ikdasm dumps, not DLLs")
    ap.add_argument("--fold", action="append", default=[], metavar="OLD=NEW",
                    help="fold a member move from type OLD into type NEW back onto OLD (repeatable)")
    ap.add_argument("--host-field", default="host", help="the service's host field name (default: host)")
    ap.add_argument("--alias-field", action="append", default=[], metavar="TYPE::NEW=OLD",
                    help="in the after build, a read of field NEW on TYPE reads field OLD (repeatable)")
    ap.add_argument("--limit", type=int, default=200, help="differences printed (default 200)")
    ap.add_argument("--show", action="append", default=[], metavar="TEXT",
                    help="print the folded body diff of each differing method whose signature holds TEXT")
    args = ap.parse_args(argv)
    folds = []
    for f in args.fold:
        if "=" not in f:
            ap.error("--fold takes OLD=NEW")
        folds.append(tuple(f.split("=", 1)))
    aliases = []
    for f in args.alias_field:
        alias = parse_alias(f)
        if alias is None:
            ap.error("--alias-field takes TYPE::NEW=OLD with two different field names")
        aliases.append(alias)
    conflicts = alias_conflicts(aliases, folds)
    if conflicts:
        ap.error("--alias-field set is not a pure renaming: " + "; ".join(conflicts))
    texts = []
    for p in (args.before, args.after):
        if args.il:
            with open(p, encoding="utf-8", errors="replace") as fh:
                texts.append(fh.read())
        else:
            texts.append(disassemble(p))
    # The before build is folded too: in a series of moves into one NEW type, the before build
    # of every step after the first already holds NEW.
    a_raw = parse_il(apply_folds(texts[0], folds, args.host_field))
    a = fold_types(a_raw, folds)
    after_text, alias_reads = apply_aliases(apply_folds(texts[1], folds, args.host_field), aliases, folds)
    b_raw = parse_il(after_text)
    b = fold_types(b_raw, folds)
    widened = widenings(a, b, [old for old, _ in folds]) if folds else []
    diffs = (fold_collisions(a_raw, folds, "before", texts[0])
             + fold_collisions(b_raw, folds, "after", texts[1])
             + alias_checks(a, b, aliases, folds) + compare(a, b, widened))
    for label, types in (("before", a), ("after", b)):
        print("%-6s types %d  fields %d  methods %d" % ((label,) + summarize(types)))
    for old, new in folds:
        if new in b_raw:
            print("folded %s onto %s: %s" % (new, old, " | ".join(sorted(b_raw[new].header))))
    for (typ, new, old), n in zip(aliases, alias_reads):
        print("aliased %s::%s onto %s: %d reads" % (typ, new, old, n))
    forwarded = [w for w in widened if w[0] == "forwarded"]
    plain = [w for w in widened if w[0] != "forwarded"]
    if plain:
        print("widened private -> internal: %d %s" % (
            len(plain), dict(sorted(collections.Counter(w[0] for w in plain).items()))))
        for kind, path, ka, kb in plain[:args.limit]:
            print("  widened %-6s %s :: %s" % (kind, path, kb[:200]))
    if forwarded:
        print("moved verbatim behind a same-named forwarder: %d" % len(forwarded))
        for kind, path, ka, kb in forwarded[:args.limit]:
            print("  forwarded %s :: %s" % (path, kb[:200]))
    counts = collections.Counter(d[0] for d in diffs)
    print("differences: %d %s" % (len(diffs), dict(sorted(counts.items())) if diffs else ""))
    for kind, path, detail in diffs[:args.limit]:
        print("  %-18s %s :: %s" % (kind, path, detail[:200]))
    for kind, path, detail in diffs:
        if kind not in ("method-body", "method-forwarder") or not any(s in detail for s in args.show):
            continue
        ba = [BODIES[h] for h in sorted(a[path].methods[detail])]
        bb = [BODIES[h] for h in sorted(b[path].methods[detail])]
        print("\n=== %s :: %s" % (path, detail[:160]))
        for x, y in zip(ba, bb):
            sys.stdout.writelines(l + "\n" for l in difflib.unified_diff(
                x.split("\n"), y.split("\n"), "before", "after", n=2, lineterm=""))
    return 1 if diffs else 0


if __name__ == "__main__":
    sys.exit(main())
