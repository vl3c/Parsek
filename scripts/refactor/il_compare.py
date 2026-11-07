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
- IL offsets (``IL_0000:``) and ``// end of method`` trailers are dropped;
- attribute blobs (hex byte rows) are dropped, because a state machine attribute spells the
  iterator class name with its ordinal.

``--fold OLD=NEW`` (Phase B, repeatable) folds a member move from type OLD into type NEW
back onto OLD before comparing: every token ``NEW::`` reads ``OLD::``, every method and
field declared in NEW is keyed under OLD, ``ldarg.0`` + ``ldfld class OLD NEW::<host>`` (a
moved body reaching the host) reads ``ldarg.0``, and ``ldarg.0`` + ``call instance class NEW
OLD::get_*`` (a host body reaching the service through its lazy accessor) reads ``ldarg.0``, and
a ``callvirt instance`` on OLD or NEW reads ``call`` (``this.M()`` compiles to call, ``host.M()``
and ``service.M()`` to callvirt).
Whatever still differs after the fold is listed: the forwarders, the accessor, the host field
and constructor, widened accessibility. A PR lists each of those by name.

Exit code 0 when nothing differs, 1 otherwise, 2 on a usage or tool error.
Plain stdlib.
"""

import argparse
import collections
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
    (re.compile(r"(?m)^\s*(?:[0-9A-F]{2} )+\)?\s*(?://.*)?$"), ""),
]


_STRING = re.compile(r'"(?:[^"\\]|\\.)*"')


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


def normalize_body(text):
    for rx, rep in BODY_ONLY:
        text = rx.sub(rep, text)
    text = normalize_name(text)
    return "\n".join(l for l in (x.strip() for x in text.split("\n")) if l)


def apply_folds(text, folds, host_field):
    """Rewrite NEW-type tokens back to OLD and drop the host hop instructions."""
    for old, new in folds:
        o, n = re.escape(old), re.escape(new)
        # A moved body reaching the host: ldarg.0; ldfld class OLD NEW::host
        text = re.sub(r"(ldarg\.0\s*\n\s*(?:IL_[0-9a-f]{4}:\s*)?)ldfld\s+class\s+%s\s+%s::%s\s*\n"
                      % (o, n, re.escape(host_field)), r"\1", text)
        # A host body reaching the service: ldarg.0; call instance class NEW OLD::get_X()
        text = re.sub(r"(ldarg\.0\s*\n\s*(?:IL_[0-9a-f]{4}:\s*)?)call\s+instance\s+class\s+%s\s+%s::get_\w+\(\)\s*\n"
                      % (n, o), r"\1", text)
        # this.M() compiles to call, service.M() / host.M() to callvirt.
        text = re.sub(r"callvirt(\s+instance\s+[^\n]*?\s(?:%s|%s)::)" % (o, n), r"call\1", text)
        text = re.sub(r"%s::" % n, "%s::" % old, text)
    return text


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
                types[path].methods[sig].append(hashlib.sha256(normalize_body(body).encode()).hexdigest())
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
            types[stack[-1][0]].fields[normalize_name(" ".join(s.split()))] += 1
        elif stack and (s.startswith(".property") or s.startswith(".event")):
            types[stack[-1][0]].props[normalize_name(" ".join(s.split()))] += 1
        depth += brace_delta(s)
        while stack and depth < stack[-1][1]:
            stack.pop()
    return types


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
        if target == path:
            dst.header.update(info.header)
        dst.fields.update(info.fields)
        dst.props.update(info.props)
        for sig, bodies in info.methods.items():
            dst.methods[sig].extend(bodies)
    return out


def compare(a, b):
    """Return a list of (kind, type path, detail) differences."""
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
                diffs.append(("method-body", path, sig))
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
    ap.add_argument("--limit", type=int, default=200, help="differences printed (default 200)")
    args = ap.parse_args(argv)
    folds = []
    for f in args.fold:
        if "=" not in f:
            ap.error("--fold takes OLD=NEW")
        folds.append(tuple(f.split("=", 1)))
    texts = []
    for p in (args.before, args.after):
        if args.il:
            with open(p, encoding="utf-8", errors="replace") as fh:
                texts.append(fh.read())
        else:
            texts.append(disassemble(p))
    a = parse_il(apply_folds(texts[0], folds, args.host_field))
    b = fold_types(parse_il(apply_folds(texts[1], folds, args.host_field)), folds)
    diffs = compare(a, b)
    for label, types in (("before", a), ("after", b)):
        print("%-6s types %d  fields %d  methods %d" % ((label,) + summarize(types)))
    counts = collections.Counter(d[0] for d in diffs)
    print("differences: %d %s" % (len(diffs), dict(sorted(counts.items())) if diffs else ""))
    for kind, path, detail in diffs[:args.limit]:
        print("  %-18s %s :: %s" % (kind, path, detail[:200]))
    return 1 if diffs else 0


if __name__ == "__main__":
    sys.exit(main())
