#!/usr/bin/env python3
"""Fold the small ParsekFlight partial files back into ParsekFlight.cs.

A pure file move (wave 2, W8a). Each file in FOLDED holds one ``public partial class
ParsekFlight`` block and nothing else; its class body is copied verbatim to the end of the
class in ``ParsekFlight.cs``, in FOLDED order, and the file is deleted. A comment above the
class declaration in a folded file (a one-line area description or an exemption note) moves
with the body, indented one level so it sits at class level. A ``using`` a folded file needs
and ``ParsekFlight.cs`` lacks is added to the main file's using block. Nothing inside a member
is edited.

Before writing anything the script verifies that every folded file has exactly the expected
shape (usings, ``namespace Parsek``, optional comments, the class block, closing braces),
that the multiset of non-blank lines of the result equals the inputs' minus only the
removed file wrappers (usings, namespace and class lines and their braces) and with the
moved comments re-indented, and that braces balance. Any mismatch fails loudly and
nothing is written. A second run refuses (the folded files are gone).

    python3 scripts/refactor/fold_parsekflight_partials.py [--check]

Plain stdlib, self-contained.
"""
import argparse
import collections
import os
import sys

REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
SRC = os.path.join(REPO_ROOT, "Source", "Parsek")
MAIN = "ParsekFlight.cs"
CLASS_LINE = "    public partial class ParsekFlight"
MAIN_CLASS_LINE = "    public partial class ParsekFlight : MonoBehaviour"

# The ParsekFlight partial files under 200 lines (plan W8 8a), folded in this order.
FOLDED = [
    "ParsekFlight.Accessors.cs",
    "ParsekFlight.BlockedChainTipGhost.cs",
    "ParsekFlight.BreakupChildSeed.cs",
    "ParsekFlight.ClaimedVesselHoldGhost.cs",
    "ParsekFlight.Flags.cs",
    "ParsekFlight.GroundPartPlacement.cs",
    "ParsekFlight.PostSwitchAutoRecord.cs",
    "ParsekFlight.Spawning.cs",
    "ParsekFlight.SplitEvents.cs",
    "ParsekFlight.TailLift.cs",
]


def fail(msg):
    sys.stderr.write("fold_parsekflight_partials: FAILED: " + msg + "\n")
    sys.exit(1)


def read(path):
    with open(path, "rb") as f:
        raw = f.read()
    if raw.startswith(b"\xef\xbb\xbf"):
        fail(path + " starts with a BOM; this script expects none")
    text = raw.decode("utf-8")
    if "\r" in text:
        fail(path + " has CR line endings")
    return text


def parse_folded(name):
    lines = read(os.path.join(SRC, name)).split("\n")
    if lines[-1] != "":
        fail(name + " does not end with a newline")
    lines = lines[:-1]
    i = 0
    usings = []
    while i < len(lines) and (lines[i].startswith("using ") or lines[i] == ""):
        if lines[i]:
            usings.append(lines[i])
        i += 1
    if lines[i:i + 2] != ["namespace Parsek", "{"]:
        fail(name + ": expected 'namespace Parsek' / '{' at line %d" % (i + 1))
    i += 2
    comments = []
    while lines[i] != CLASS_LINE:
        if not lines[i].startswith("    //"):
            fail(name + ": unexpected line above the class: " + repr(lines[i]))
        comments.append(lines[i])
        i += 1
    if lines[i + 1] != "    {":
        fail(name + ": class brace not on its own line")
    body = lines[i + 2:]
    if body[-2:] != ["    }", "}"]:
        fail(name + ": file does not end with the class and namespace braces")
    body = body[:-2]
    while body and body[-1] == "":
        body.pop()
    while body and body[0] == "":
        body.pop(0)
    for b in body:
        if b and not b.startswith("        "):
            fail(name + ": body line not at member indentation: " + repr(b))
    wrapper = usings + ["namespace Parsek", "{", CLASS_LINE, "    {", "    }", "}"]
    return usings, comments, body, wrapper


def build():
    main_path = os.path.join(SRC, MAIN)
    main = read(main_path)
    for name in FOLDED:
        if not os.path.exists(os.path.join(SRC, name)):
            fail(name + " not found (already folded?)")
    mlines = main.split("\n")
    if mlines[-1] != "":
        fail(MAIN + " does not end with a newline")
    mlines = mlines[:-1]
    if mlines[-2:] != ["    }", "}"]:
        fail(MAIN + " does not end with the class and namespace braces")
    if mlines.count(MAIN_CLASS_LINE) != 1:
        fail(MAIN + ": class declaration not found once")
    main_usings = [l for l in mlines[:mlines.index("namespace Parsek")] if l.startswith("using ")]

    parsed = [(n,) + parse_folded(n) for n in FOLDED]
    added_usings = []
    for _, usings, _, _, _ in parsed:
        for u in usings:
            if u not in main_usings and u not in added_usings:
                added_usings.append(u)

    out = list(mlines[:-2])
    for name, _, comments, body, _ in parsed:
        out.append("")
        out.extend("    " + c for c in comments)
        out.extend(body)
    out.extend(["    }", "}"])
    if added_usings:
        last = max(i for i, l in enumerate(out) if l.startswith("using ") and i < out.index("namespace Parsek"))
        out[last + 1:last + 1] = sorted(added_usings)
    result = "\n".join(out) + "\n"

    # Verification: non-blank multiset.
    before = collections.Counter(l for l in mlines if l.strip())
    for name, _, comments, body, wrapper in parsed:
        flines = read(os.path.join(SRC, name)).split("\n")
        c = collections.Counter(l for l in flines if l.strip())
        c.subtract(collections.Counter(wrapper))
        for cm in comments:
            c[cm] -= 1
            c["    " + cm] += 1
        before.update({k: v for k, v in c.items() if v})
        if any(v < 0 for v in c.values()):
            fail(name + ": wrapper accounting went negative")
    for u in added_usings:
        before[u] += 1
    after = collections.Counter(l for l in result.split("\n") if l.strip())
    before = +before
    if before != after:
        diff = (before - after) + (after - before)
        fail("non-blank line multiset differs: " + repr(list(diff.items())[:10]))
    depth = 0
    for ch in result:
        if ch == "{":
            depth += 1
        elif ch == "}":
            depth -= 1
            if depth < 0:
                break
    # Braces inside strings and chars are rare here; a strict count over the original is the
    # comparison that matters.
    orig_depth = main.count("{") - main.count("}")
    if result.count("{") - result.count("}") != orig_depth:
        fail("brace balance changed")
    return result, added_usings, parsed


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true", help="verify only, write nothing")
    ap.add_argument("--root", default=REPO_ROOT, help="repository root (default: %(default)s)")
    args = ap.parse_args()
    global SRC
    SRC = os.path.join(os.path.abspath(args.root), "Source", "Parsek")
    result, added, parsed = build()
    for name, _, _, body, _ in parsed:
        print("%-45s %4d body lines" % (name, len(body)))
    print("usings added to %s: %s" % (MAIN, ", ".join(added) if added else "none"))
    if args.check:
        print("verified (check only): non-blank line multiset preserved; braces balance")
        return
    with open(os.path.join(SRC, MAIN), "w", encoding="utf-8", newline="") as f:
        f.write(result)
    for name in FOLDED:
        os.remove(os.path.join(SRC, name))
    print("verified: non-blank line multiset preserved; braces balance; %d files folded into %s"
          % (len(FOLDED), MAIN))


if __name__ == "__main__":
    main()
