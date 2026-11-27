#!/usr/bin/env python3
"""One-off: seed scripts/arch/far_watch_sites.toml from the far-watch site audits.

design-far-watch.md section 11.4. Audits A and B decide every
`FlightGlobals.ActiveVessel` / `FlightGlobals.fetch.activeVessel` read outside
InGameTests/, audit C every `IsGhostMapVessel` / `ghostMapVesselPids` hit, each
row by `file:line` at the audits' commit. This script

1. parses every audit table row (site cell, decision code cell);
2. reads each named line at that commit (`git show <commit>:<path>`), comment-
   and string-stripped, and counts the pattern matches on it, so a comment row
   counts 0 and a row whose lines carry no hit of its pattern at all is reported
   (a line number the commit does not match);
3. maps each line to its enclosing member with a brace walk of the stripped
   file (lambdas and local functions belong to the method that wrote them, as
   the compiler's generated code folds into it in ilfacts), keys it the way
   ilfacts keys a method (`Type::Name(il params)`), and sums the counts per
   member and pattern;
4. compares every (member, pattern) count with the IL count of a Debug build of
   that commit (ilfacts' occurrence counts) and reports each difference, each
   IL site no row reached and each line it could not key, so the hand map below
   can name them;
5. with `--retarget`, re-counts against a second build (current main), keeps
   the codes, rewrites the counts and lists what moved.

Accepted only when step 4 reports no difference. After seeding, entries are
keyed by member, never by line (the gate is scripts/arch/test_far_watch_sites.py).

    python3 scripts/refactor/seed_far_watch_sites.py \\
        --commit ba7cd9eb --dll ../Parsek-ba7cd9eb/Source/Parsek/bin/Debug/Parsek.dll \\
        --source ../Parsek-ba7cd9eb/Source/Parsek --audits /mnt/project-files/far-watch/audits/ \\
        --retarget Source/Parsek/bin/Debug/Parsek.dll --retarget-source Source/Parsek \\
        --out scripts/arch/far_watch_sites.toml

Plain stdlib; it runs once, so it carries no tests of its own beyond the gate's.
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from collections import Counter, defaultdict
from pathlib import Path

ARCH = Path(__file__).resolve().parents[1] / "arch"
sys.path.insert(0, str(ARCH))

import archview  # noqa: E402
import far_watch_sites as fws  # noqa: E402
import ilfacts  # noqa: E402
import ilgraph  # noqa: E402

REPO = Path(__file__).resolve().parents[2]
SOURCE_PREFIX = "Source/Parsek/"

# Source regexes over comment- and string-stripped text, one per gate pattern.
SOURCE_PATTERNS = {
    # No left word boundary: archview's stripper joins interpolation holes
    # with no separator (`{a}, {FlightGlobals.X}` reads `aFlightGlobals.X`).
    "ActiveVessel": re.compile(r"FlightGlobals\s*\.\s*(?:ActiveVessel|fetch\s*\.\s*activeVessel)\b"),
    # A call or method group; a declaration (`bool IsGhostMapVessel(`) and a
    # same-named field (`c.IsGhostMapVessel`) are not.
    "IsGhostMapVessel": re.compile(
        r"GhostMapPresence\s*\.\s*IsGhostMapVessel\b|(?<![.\w])(?<!bool )IsGhostMapVessel\s*\("),
    "ghostMapVesselPids": re.compile(r"\bghostMapVesselPids\b"),
    "wrapper": re.compile(r"(?<!Vessel )(?<![.\w])(?:GetActiveVesselSafe|ReadActiveVesselCore)\s*\("),
}

# Which pattern an audit's rows decide. Every row may also decide a wrapper call.
AUDIT_PATTERNS = {
    "A": ("ActiveVessel",),
    "B": ("ActiveVessel",),
    "C": ("IsGhostMapVessel", "ghostMapVesselPids"),
}
# Sections whose rows are not grep hits of the audit's pattern (cached-vessel
# comparisons); their rows decide wrapper calls only.
NON_GREP_SECTION_RE = re.compile(r"Separate section|Comparisons against a cached|Section 2", re.I)

# Members the brace walk keys wrongly or not at all, by (file, line) -> IL key.
# Filled from the step-4 report; every entry is listed in the seed report.
HAND_MAP = {
    # An attribute argument holds an array initializer (`new ArgumentType[] { ... }`),
    # whose braces the walk reads as a member block.
    ("Patches/PartMassivePartCheckSeederPatch.cs", 130):
        "Parsek.Patches.PartMassivePartCheckSeederPatch::PrefixMassivePartCheck(Part)",
}

# Codes for IL sites no audit row reaches (main moved after the audits, or a
# read the grep shape missed), by (IL key, pattern) -> (codes, note).
HAND_CODES = {
    ("Parsek.WatchModeController::GetActiveVesselSafe()", "wrapper"): (
        ["WATCH-INTERNAL"],
        "A WatchModeController.Runtime.cs:46 (the wrapper's own hand-off to ReadActiveVesselCore, passed as a"
        " delegate to the exception guard; the row decides the read behind it)",
    ),
    # Sites main gained after the audits' commit (PR #2842, the save active-index
    # fixes), decided here by the audit rows they mirror.
    ("Parsek.GhostMapPresence::ComputeActiveVesselIdxForStrip(System.Collections.Generic.List`1<ProtoVessel>,"
     "int32,bool)", "ghostMapVesselPids"): (
        ["DRONE-DIFFERENT"],
        "main after ba7cd9eb: the save strip's activeVesselIdx repair, which must also count the drone"
        " (as C GhostMapPresence.ProtoVesselLifecycle.cs:1295, 1299)",
    ),
    ("Parsek.GhostMapPresence::RemoveAllGhostVesselsBeforeFarSwitch(Vessel)", "IsGhostMapVessel"): (
        ["DRONE-NA"],
        "main after ba7cd9eb: the far Switch-To pre-stock ghost strip; a target is never the drone, and focus"
        " moves only between loaded vessels while it exists (7.3)",
    ),
    ("Parsek.GhostMapPresence::RemoveAllGhostVesselsBeforeFarSwitch(Vessel)", "ghostMapVesselPids"): (
        ["DRONE-NA"],
        "main after ba7cd9eb: the far Switch-To pre-stock ghost strip (ghost-set bookkeeping); no unloaded"
        " switch happens while the drone exists (7.3)",
    ),
    ("Parsek.ParsekFlight::LogFlightReadyFocusCore()", "ActiveVessel"): (
        ["UNREACHABLE"],
        "main after ba7cd9eb: the flight-ready focus log, once per flight scene load before any drone exists"
        " (as A ParsekFlight.cs:2104)",
    ),
}

# Handlers of vessel GameEvents that need no drone guard call, with the reason
# (design section 11.4, audit C section 2). Every other handler is `guard`.
HANDLER_EXEMPT = {
    "Parsek.ParsekFlight::OnVesselSwitchComplete(Vessel)":
        "real branch H8a: the drone arrival runs the background transition, then returns before consume / arm",
    "Parsek.ParsekFlight::OnVesselWillDestroy(Vessel)":
        "real branch H8b: the drone's own destroy goes to OnDroneLost",
    "Parsek.KerbalsWindowUI::OnCrewVesselChangeEvent(Vessel)":
        "marks the crew view dirty only; the drone has no crew (audit C section 2 row 12, CAMERA-OK)",
    "Parsek.KerbalsWindowUI::OnCrewVesselEvent(Vessel)":
        "marks the crew view dirty only; the drone has no crew",
    "Parsek.KerbalsWindowUI::OnCrewBoardEvent(GameEvents/FromToAction`2<Part,Part>)":
        "marks the crew view dirty only; the drone has no crew and no seat",
    "Parsek.KerbalsWindowUI::OnCrewEvaEvent(GameEvents/FromToAction`2<Part,Part>)":
        "marks the crew view dirty only; the drone has no crew",
    "Parsek.ParsekFlight::OnCrewOnEva(GameEvents/FromToAction`2<Part,Part>)":
        "the drone has no crew, so no EVA leaves it (audit C, adjacent subscribers)",
    "Parsek.ParsekFlight::OnCrewBoardVessel(GameEvents/FromToAction`2<Part,Part>)":
        "the drone has no crew and no seat, so nobody boards it (audit C, adjacent subscribers)",
    "Parsek.ParsekFlight::OnVesselsUndocking(Vessel,Vessel)":
        "the drone has no docking port and never docks (audit C, adjacent subscribers)",
    "Parsek.ParsekFlight::OnVesselSwitchingToUnloaded(Vessel,Vessel)":
        "stock fires it only for an unloaded target; far watch switches between loaded vessels only (7.3)",
    "Parsek.PostFlightDialogLogHost::OnVesselRecoveryProcessing(ProtoVessel,KSP.UI.Screens.MissionRecoveryDialog,float32)":
        "names the recovered vessel in the recovery dialog log only; the drone is never recovered",
    "Parsek.TestCommands.ParsekTestCommandAddon::OnDriveVesselPartDie(Part)":
        "TEST-SEAM observer; the seam refuses vessel verbs while far-watching (H7)",
    "Parsek.TestCommands.ParsekTestCommandAddon::OnKscRecoverRecoveredObserved(ProtoVessel,bool)":
        "TEST-SEAM observer; the seam refuses vessel verbs while far-watching (H7)",
    "Parsek.TestCommands.ParsekTestCommandAddon::OnRecoverSeamRecoveredObserved(ProtoVessel,bool)":
        "TEST-SEAM observer; the seam refuses vessel verbs while far-watching (H7)",
    "Parsek.TestCommands.ParsekTestCommandAddon::OnTsRecoverRecoveredObserved(ProtoVessel,bool)":
        "TEST-SEAM observer; the seam refuses vessel verbs while far-watching (H7)",
}

# Production methods returning Vessel that read ActiveVessel and are not reader wrappers.
NOT_WRAPPERS = {
    "Parsek.TestCommands.ParsekTestCommandAddon::ReadActiveVesselForEditorRoute()":
        "TEST-SEAM: the seam's own read for its EditorRoute verbs; the seam refuses them while far-watching (H7)",
}

C_SHARP_ALIASES = {
    "int": "int32", "uint": "uint32", "long": "int64", "ulong": "uint64", "short": "int16",
    "ushort": "uint16", "float": "float32", "double": "float64", "byte": "uint8", "sbyte": "int8",
    "bool": "bool", "string": "string", "object": "object", "char": "char", "void": "void",
    "decimal": "Decimal",
}


# --- audit rows -------------------------------------------------------------

SITE_FILE_RE = re.compile(r"((?:[\w.]+/)*[\w.]+\.cs):((?:\s*(?:\(\+)?\d+(?:-\d+)?\)?\s*[,/]?)+)")


def parse_site_cell(cell):
    """[(file text, [single line numbers])]; ranges (`55-275`) are spans, not hits, and are dropped."""
    out = []
    for match in SITE_FILE_RE.finditer(cell):
        numbers = re.sub(r"\d+-\d+", " ", match.group(2))
        lines = [int(n) for n in re.findall(r"\d+", numbers)]
        out.append((match.group(1), lines))
    return out


def parse_audit(name, text):
    """Rows of one audit: dicts with audit, section, site, lines [(file, [n])], codes."""
    rows = []
    lines = text.splitlines()
    header = None
    section = ""
    for index, line in enumerate(lines):
        if line.startswith("#"):
            section = line.lstrip("#").strip()
            header = None
            continue
        if not line.startswith("|"):
            header = None
            continue
        cells = [c.strip() for c in re.split(r"(?<!\\)\|", line.strip().strip("|"))]
        if index + 1 < len(lines) and re.match(r"^\|\s*-", lines[index + 1]):
            header = [c.lower() for c in cells]
            continue
        if header is None or re.match(r"^-+$", cells[0]):
            continue
        if header[0] not in ("file:line", "site") or "code" not in header:
            continue
        code_cell = cells[header.index("code")] if len(cells) > header.index("code") else ""
        rows.append({
            "audit": name,
            "section": section,
            "site": cells[0],
            "files": parse_site_cell(cells[0]),
            "codes": sorted(set(re.findall(r"\b(?:" + "|".join(map(re.escape, fws.CODES)) + r")\b", code_cell))),
            "grep": not NON_GREP_SECTION_RE.search(section),
        })
    return rows


# --- source at the commit ---------------------------------------------------


def git_files(commit):
    out = subprocess.run(["git", "-C", str(REPO), "ls-tree", "-r", "--name-only", commit, SOURCE_PREFIX],
                         check=True, capture_output=True, text=True).stdout
    return [p for p in out.splitlines() if p.endswith(".cs")]


def git_text(commit, path):
    return subprocess.run(["git", "-C", str(REPO), "show", "%s:%s" % (commit, path)],
                          check=True, capture_output=True, text=True).stdout


def resolve_file(text, audit, files):
    """The repo path an audit names (`Recording/X.cs`, or a bare `X.cs`, Controllers/ first for A)."""
    exact = SOURCE_PREFIX + text
    if exact in files:
        return exact
    base = text.split("/")[-1]
    candidates = [f for f in files if f.split("/")[-1] == base]
    if len(candidates) > 1 and audit == "A":
        candidates = [f for f in candidates if f.startswith(SOURCE_PREFIX + "Controllers/")] or candidates
    if len(candidates) > 1:
        candidates = [f for f in candidates if "/InGameTests/" not in f] or candidates
    if len(candidates) == 1:
        return candidates[0]
    # A row may drop the partial's prefix (`RecorderSession.cs` for `ParsekFlight.RecorderSession.cs`).
    candidates = [f for f in files if f.endswith("." + base) and "/InGameTests/" not in f]
    if audit == "A":
        candidates = [f for f in candidates if f.startswith(SOURCE_PREFIX + "Controllers/")] or candidates
    return candidates[0] if len(candidates) == 1 else None


# --- the brace walk ---------------------------------------------------------

TYPE_RE = re.compile(r"\b(class|struct|interface|enum|record)\s+(@?[A-Za-z_]\w*)")
NAMESPACE_DECL_RE = re.compile(r"\bnamespace\s+([A-Za-z_][\w.]*)")
ACCESSOR_RE = re.compile(r"^(?:(?:private|protected|internal|public|readonly)\s+)*(get|set|init|add|remove)$")


def _top_level(text, ch):
    """Index of the first `ch` outside (), [], <> nesting, or -1."""
    depth = 0
    for i, c in enumerate(text):
        if c == ch and depth == 0:
            return i
        if c in "([":
            depth += 1
        elif c in ")]":
            depth -= 1
    return -1


MODIFIERS = frozenset(
    "public private protected internal static readonly async unsafe extern override virtual abstract sealed"
    " new partial ref volatile".split()
)


def _drop_trailing_generic(text):
    if not text.endswith(">"):
        return text
    depth = 0
    for i in range(len(text) - 1, -1, -1):
        if text[i] == ">":
            depth += 1
        elif text[i] == "<":
            depth -= 1
            if depth == 0:
                return text[:i].rstrip()
    return text


def _name_paren(header):
    """Index of the `(` that opens a method's parameter list: the first one outside
    (), [], <> nesting whose preceding word is a name, not a modifier (a tuple
    return type `(bool, Vessel)` opens earlier), or -1."""
    depth = 0
    for i, c in enumerate(header):
        if c == "(" and depth == 0:
            before = _drop_trailing_generic(header[:i].rstrip())
            match = re.search(r"(~?@?[A-Za-z_]\w*)$", before)
            if match and match.group(1) not in MODIFIERS:
                return i
        if c in "([<":
            depth += 1
        elif c in ")]>":
            depth -= 1
    return -1


def _strip_attributes(header):
    header = header.strip()
    while header.startswith("["):
        depth = 0
        for i, c in enumerate(header):
            if c == "[":
                depth += 1
            elif c == "]":
                depth -= 1
                if depth == 0:
                    header = header[i + 1:].strip()
                    break
        else:
            break
    return header


def _top_level_assign(text):
    """Index of a top-level `=` that is an assignment (not `=>`, `==`, `<=`, `>=`, `!=`), or -1."""
    depth = 0
    for i, c in enumerate(text):
        if c in "([{":
            depth += 1
        elif c in ")]}":
            depth -= 1
        elif c == "=" and depth == 0:
            prev = text[i - 1] if i else ""
            nxt = text[i + 1] if i + 1 < len(text) else ""
            if nxt in "=>" or prev in "=<>!+-*/%&|^?":
                continue
            return i
    return -1


def _split_params(text):
    parts, depth, start = [], 0, 0
    for i, c in enumerate(text):
        if c in "(<[":
            depth += 1
        elif c in ")>]":
            depth -= 1
        elif c == "," and depth == 0:
            parts.append(text[start:i])
            start = i + 1
    if text[start:].strip():
        parts.append(text[start:])
    return [p.strip() for p in parts if p.strip()]


def simplify_cs_param(text):
    """A C# parameter's type reduced to its last name segment, IL aliases, & and []."""
    text = _strip_attributes(text)
    text = text.split("=", 1)[0].strip()
    words = text.split()
    byref = False
    while words and words[0] in ("this", "ref", "out", "in", "params", "scoped", "readonly"):
        byref = byref or words[0] in ("ref", "out", "in")
        words = words[1:]
    type_text = " ".join(words[:-1]) if len(words) > 1 else (words[0] if words else "")
    return simplify_type(type_text) + ("&" if byref else "")


def simplify_type(text):
    text = text.strip()
    array = ""
    while text.endswith("[]"):
        array += "[]"
        text = text[:-2].strip()
    if text.endswith("?"):
        return "Nullable" + array
    if text.startswith("("):
        return "ValueTuple" + array
    head = text.split("<", 1)[0]
    head = re.split(r"[./]", head)[-1].split("`", 1)[0].strip()
    return C_SHARP_ALIASES.get(head, head) + array


def simplify_il_param(text):
    byref = text.endswith("&")
    text = text.rstrip("&").strip()
    for prefix in ("class ", "valuetype "):
        if text.startswith(prefix):
            text = text[len(prefix):]
    array = ""
    while text.endswith("[]"):
        array += "[]"
        text = text[:-2]
    head = text.split("<", 1)[0]
    head = re.split(r"[./]", head)[-1].split("`", 1)[0]
    if head == "Nullable":
        return "Nullable" + array + ("&" if byref else "")
    return head + array + ("&" if byref else "")


def _member_from_header(header, type_name, is_static_hint=False):
    """(il name, [simplified params]) for a method / ctor header, (name, None) for a property, or None."""
    header = _strip_attributes(header)
    paren = _name_paren(header)
    if "operator" in header.split("(")[0].split():
        return None
    if paren >= 0:
        before = header[:paren].rstrip()
        before = _drop_trailing_generic(before)  # a generic method's <T>
        match = re.search(r"((?:@?[A-Za-z_]\w*\.)*)(~?@?[A-Za-z_]\w*)$", before)
        if not match:
            return None
        name = match.group(2).lstrip("@")
        if match.group(1):
            # An explicit interface implementation: IL names it `Ns.IFace.Name`.
            name = match.group(1).replace("@", "") + name
        depth = 0
        close = len(header)
        for i in range(paren, len(header)):
            if header[i] == "(":
                depth += 1
            elif header[i] == ")":
                depth -= 1
                if depth == 0:
                    close = i
                    break
        params = [simplify_cs_param(p) for p in _split_params(header[paren + 1:close])]
        words = before[: match.start()].split()
        if name == type_name:
            name = ".cctor" if "static" in words else ".ctor"
        elif name.startswith("~"):
            name = "Finalize"
        return name, params
    if re.search(r"\bthis\s*\[", header):
        return "Item", None
    match = re.search(r"(@?[A-Za-z_]\w*)\s*$", header)
    if not match:
        return None
    return match.group(1).lstrip("@"), None


def walk_members(stripped):
    """[(start, end, ns, type chain, il name, params or None, kind)] member spans of a stripped file.

    kind: method, accessor, init (a field initializer: .cctor / .ctor), unknown.
    """
    spans = []
    stack = []  # entries: {kind, name?, chain?, start, brace}
    namespace_file = None
    seg = 0
    ns_stack = []

    def current_type():
        for entry in reversed(stack):
            if entry["kind"] == "type":
                return entry
        return None

    def namespace():
        names = [e["name"] for e in stack if e["kind"] == "ns"]
        if namespace_file:
            names = [namespace_file] + names
        return ".".join(names)

    def member_level():
        return stack and stack[-1]["kind"] == "type"

    def statement_member(text, start, end, top):
        text = _strip_attributes(text.strip())
        if not text:
            return
        type_entry = current_type()
        if top["kind"] == "prop":
            arrow = text.find("=>")
            accessor = text[:arrow].strip() if arrow >= 0 else text.strip()
            if ACCESSOR_RE.match(accessor) and arrow >= 0:
                verb = ACCESSOR_RE.match(accessor).group(1)
                spans.append((start, end, namespace(), type_entry["chain"], "%s_%s" % (verb, top["name"]),
                               top.get("params"), "accessor"))
            elif arrow < 0 and text.startswith("="):
                pass
            return
        arrow = -1
        depth = 0
        for i in range(len(text) - 1):
            c = text[i]
            if c in "([":
                depth += 1
            elif c in ")]":
                depth -= 1
            elif c == "=" and text[i + 1] == ">" and depth == 0:
                arrow = i
                break
        assign = _top_level_assign(text)
        if arrow >= 0 and (assign < 0 or arrow < assign):
            member = _member_from_header(text[:arrow], type_entry["name"])
            if member is None:
                return
            name, params = member
            if params is None:
                name, params, kind = "get_" + name, [], "accessor"
            else:
                kind = "method"
            spans.append((start, end, namespace(), type_entry["chain"], name, params, kind))
            return
        if assign >= 0:
            words = text[:assign].split()
            spans.append((start, end, namespace(), type_entry["chain"],
                          ".cctor" if "static" in words or "const" in words else ".ctor", None, "init"))

    i = 0
    n = len(stripped)
    while i < n:
        c = stripped[i]
        if c == "{":
            header = stripped[seg:i]
            top = stack[-1] if stack else None
            entry = {"start": i, "brace": True}
            if top is None or top["kind"] == "ns":
                type_match = TYPE_RE.search(header)
                ns_match = NAMESPACE_DECL_RE.search(header)
                if type_match:
                    parent_chain = current_type()["chain"] if current_type() else []
                    entry.update(kind="type", name=type_match.group(2).lstrip("@"),
                                 chain=parent_chain + [type_match.group(2).lstrip("@")],
                                 enum=type_match.group(1) == "enum")
                elif ns_match:
                    entry.update(kind="ns", name=ns_match.group(1))
                else:
                    entry.update(kind="other")
            elif top["kind"] == "type":
                clean = _strip_attributes(header.strip())
                type_match = TYPE_RE.search(clean)
                has_arrow = "=>" in clean
                assign = _top_level_assign(clean)
                if top.get("enum"):
                    entry.update(kind="other")
                elif type_match and not has_arrow and assign < 0 and _top_level(clean, "(") < 0 \
                        or (type_match and re.match(r"^[\w\s]*\b(class|struct|interface|enum|record)\b", clean)):
                    entry.update(kind="type", name=type_match.group(2).lstrip("@"),
                                 chain=top["chain"] + [type_match.group(2).lstrip("@")],
                                 enum=type_match.group(1) == "enum")
                elif has_arrow or assign >= 0:
                    # A statement member whose lambda opens a block: it ends at the `;`.
                    stack.append({"kind": "stmt", "start": seg, "brace": False})
                    entry.update(kind="inner")
                else:
                    member = _member_from_header(clean, top["name"])
                    if member is None:
                        entry.update(kind="unknown")
                    elif member[1] is None:
                        entry.update(kind="prop", name=member[0], params=None)
                    else:
                        entry.update(kind="method", name=member[0], params=member[1], member_start=seg)
            elif top["kind"] == "prop":
                accessor = ACCESSOR_RE.match(_strip_attributes(header.strip()))
                if accessor:
                    entry.update(kind="accessor", name="%s_%s" % (accessor.group(1), top["name"]),
                                 params=[] if accessor.group(1) == "get" else None, member_start=seg)
                else:
                    entry.update(kind="inner")
            else:
                entry.update(kind="inner")
            stack.append(entry)
            seg = i + 1
        elif c == "}":
            while stack and not stack[-1]["brace"]:
                stack.pop()
            if stack:
                entry = stack.pop()
                if entry["kind"] in ("method", "accessor"):
                    type_entry = current_type()
                    spans.append((entry.get("member_start", entry["start"]), i, namespace(), type_entry["chain"],
                                  entry["name"], entry["params"], entry["kind"]))
            seg = i + 1
        elif c == ";":
            top = stack[-1] if stack else None
            if top is not None and top["kind"] == "stmt":
                stack.pop()
                owner = stack[-1] if stack else None
                if owner is not None and owner["kind"] in ("type", "prop"):
                    statement_member(stripped[top["start"]:i], top["start"], i, owner)
            elif top is not None and top["kind"] in ("type", "prop"):
                statement_member(stripped[seg:i], seg, i, top)
            elif top is None or top["kind"] == "ns":
                ns_match = NAMESPACE_DECL_RE.search(stripped[seg:i])
                if ns_match and top is None:
                    namespace_file = ns_match.group(1)
            seg = i + 1
        i += 1
    return spans


def member_at(spans, offset):
    best = None
    for span in spans:
        if span[0] <= offset <= span[1]:
            if best is None or span[1] - span[0] < best[1] - best[0]:
                best = span
    # An accessor or method span inside a property: the innermost wins; a
    # method never nests in a method (lambdas are not spans).
    return best


class IlIndex:
    def __init__(self, facts):
        self.facts = facts
        self.by_type_name = defaultdict(list)
        self.by_type = defaultdict(list)
        for key, entry in facts["methods"].items():
            self.by_type_name[(entry["type"], entry["name"])].append(key)
            self.by_type[entry["type"]].append(key)

    def key_for(self, span):
        _start, _end, ns, chain, name, params, kind = span
        type_path = (ns + "." if ns else "") + "/".join(chain)
        keys = self.by_type_name.get((type_path, name), [])
        if not keys and "." in name:
            # An explicit interface implementation: the IL name carries the namespace too.
            keys = [k for k in self.by_type.get(type_path, ())
                    if self.facts["methods"][k]["name"].endswith("." + name)]
        if kind == "init":
            return keys[0] if len(keys) == 1 else (keys or [None])[0] if len(keys) == 1 else None
        if len(keys) == 1:
            return keys[0]
        if params is None:
            return None
        fit = [k for k in keys if len(fws.split_params(k)) == len(params)]
        if len(fit) == 1:
            return fit[0]
        exact = [k for k in fit if [simplify_il_param(p) for p in fws.split_params(k)] == params]
        return exact[0] if len(exact) == 1 else None


# --- seeding ------------------------------------------------------------------


def source_hits(commit, path, cache):
    """(stripped, spans, {line: Counter(pattern)}) for one file at the commit."""
    if path not in cache:
        text = git_text(commit, path)
        drops = []
        stripped = archview.strip_comments_and_strings(text, drops)
        line_at = archview.line_index(stripped, drops)
        hits = defaultdict(Counter)
        offsets = defaultdict(list)
        for pattern, regex in SOURCE_PATTERNS.items():
            for match in regex.finditer(stripped):
                line = line_at(match.start())
                hits[line][pattern] += 1
                offsets[(line, pattern)].append(match.start())
        cache[path] = (text, stripped, walk_members(stripped), hits, offsets)
    return cache[path]


def il_counts(facts, patterns):
    """{(key, pattern): count} over production methods (every module but InGameTests)."""
    out = {}
    for key, entry in facts["methods"].items():
        if not fws.is_production(facts, entry):
            continue
        for pattern, count in fws.method_counts(entry, patterns).items():
            if count:
                out[(key, pattern)] = count
    return out


def seed(commit, facts, audits_dir, report):
    files = git_files(commit)
    rows = []
    for name in ("A", "B", "C"):
        path = next(Path(audits_dir).glob(name + "-*.md"))
        rows.extend(parse_audit(name, path.read_text(encoding="utf-8")))
    report.append("audit rows parsed: %d (%s)" % (
        len(rows), ", ".join("%s %d" % (a, sum(1 for r in rows if r["audit"] == a)) for a in "ABC")))
    index = IlIndex(facts)
    cache = {}
    counts = Counter()
    codes = defaultdict(set)
    audit_refs = defaultdict(list)
    seen = set()
    unkeyed = []
    no_hit_rows = []
    for row in rows:
        patterns = list(AUDIT_PATTERNS[row["audit"]]) if row["grep"] else []
        patterns.append("wrapper")
        row_hits = Counter()
        for file_text, lines in row["files"]:
            path = resolve_file(file_text, row["audit"], files)
            if path is None:
                report.append("UNRESOLVED FILE %s (%s row %s)" % (file_text, row["audit"], row["site"]))
                continue
            text, _stripped, spans, hits, offsets = source_hits(commit, path, cache)
            raw_lines = text.splitlines()
            for line in lines:
                for pattern in patterns:
                    raw = raw_lines[line - 1] if 0 < line <= len(raw_lines) else ""
                    if SOURCE_PATTERNS[pattern].search(raw.split("//", 1)[0]) or SOURCE_PATTERNS[pattern].search(raw):
                        row_hits[pattern] += 1
                    count = hits.get(line, {}).get(pattern, 0)
                    if not count or (path, line, pattern) in seen:
                        continue
                    seen.add((path, line, pattern))
                    hand = HAND_MAP.get((path[len(SOURCE_PREFIX):], line))
                    if hand:
                        key = hand
                    else:
                        span = member_at(spans, offsets[(line, pattern)][0])
                        key = index.key_for(span) if span else None
                    if key is None:
                        unkeyed.append((path, line, pattern, row["site"]))
                        continue
                    counts[(key, pattern)] += count
                    codes[(key, pattern)] |= set(row["codes"])
                    ref = "%s %s" % (row["audit"], row["site"])
                    if ref not in audit_refs[(key, pattern)]:
                        audit_refs[(key, pattern)].append(ref)
        if row["grep"]:
            for pattern in AUDIT_PATTERNS[row["audit"]]:
                pass
            if not any(row_hits[p] for p in AUDIT_PATTERNS[row["audit"]]):
                no_hit_rows.append(row)
    # Source hits no row named.
    unaudited = []
    for path, (_text, _stripped, spans, hits, offsets) in sorted(cache.items()):
        for line, by_pattern in hits.items():
            for pattern in by_pattern:
                if (path, line, pattern) not in seen:
                    unaudited.append((path, line, pattern))
    return counts, codes, audit_refs, unkeyed, no_hit_rows, unaudited, cache


def compare(seeded, il):
    """(matched keys, [(key, pattern, seeded, il)] differences)."""
    diffs = []
    for site in sorted(set(seeded) | set(il)):
        if seeded.get(site, 0) != il.get(site, 0):
            diffs.append((site[0], site[1], seeded.get(site, 0), il.get(site, 0)))
    return diffs


def load_facts(path, source):
    path = Path(path)
    if path.suffix == ".json":
        return json.loads(path.read_text(encoding="utf-8"))
    rules, _tooling, _f, _a = archview.load_rules(archview.DEFAULT_MODULES)
    return ilfacts.build(ilgraph.load_parsed(path), ilfacts.source_type_modules(source, rules))


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    parser.add_argument("--commit", required=True)
    parser.add_argument("--dll", required=True, help="Debug Parsek.dll (or an ilfacts --json dump) of --commit")
    parser.add_argument("--source", required=True, help="Source/Parsek of a checkout of --commit (module map)")
    parser.add_argument("--audits", required=True)
    parser.add_argument("--retarget", help="Debug Parsek.dll (or facts json) to re-count against")
    parser.add_argument("--retarget-source")
    parser.add_argument("--retarget-label", default="main")
    parser.add_argument("--out")
    args = parser.parse_args(argv)
    report = []
    facts = load_facts(args.dll, args.source)
    il = il_counts(facts, fws.DEFAULT_PATTERNS)
    seeded, codes, refs, unkeyed, no_hit_rows, unaudited, _cache = seed(args.commit, facts, args.audits, report)
    for path, line, pattern, site in unkeyed:
        report.append("UNKEYED %s:%d %s (row %s)" % (path, line, pattern, site))
    for row in no_hit_rows:
        report.append("ROW WITHOUT A HIT OF ITS PATTERN (%s, %s): %s codes=%s" % (
            row["audit"], row["section"], row["site"], row["codes"]))
    for path, line, pattern in unaudited:
        report.append("UNAUDITED SOURCE HIT %s:%d %s" % (path, line, pattern))
    diffs = compare(seeded, il)
    for key, pattern, s, i in diffs:
        report.append("MISMATCH %s %s seeded=%d il=%d" % (pattern, key, s, i))
    report.append("seeded sites: %d, IL sites: %d, mismatches: %d" % (len(seeded), len(il), len(diffs)))
    for (path, line), key in sorted(HAND_MAP.items()):
        report.append("HAND-MAPPED %s:%d -> %s" % (path, line, key))
    entries = {}
    for site in il:
        key, pattern = site
        if site in seeded and seeded[site] == il[site]:
            entries[site] = {"count": il[site], "codes": sorted(codes[site]), "audit": "; ".join(refs[site])}
        elif site in HAND_CODES:
            hand_codes, note = HAND_CODES[site]
            entries[site] = {"count": il[site], "codes": list(hand_codes), "audit": note}
            report.append("HAND-CODED %s %s count=%d codes=%s (%s)" % (pattern, key, il[site], hand_codes, note))
    target_report = []
    if args.retarget:
        target = load_facts(args.retarget, args.retarget_source)
        il_target = il_counts(target, fws.DEFAULT_PATTERNS)
        retargeted = {}
        for site, count in sorted(il_target.items()):
            if site in entries:
                entry = dict(entries[site])
                if entry["count"] != count:
                    target_report.append("COUNT %s %s %d -> %d" % (site[1], site[0], entry["count"], count))
                entry["count"] = count
                retargeted[site] = entry
            elif site in HAND_CODES:
                hand_codes, note = HAND_CODES[site]
                retargeted[site] = {"count": count, "codes": list(hand_codes), "audit": note}
                target_report.append("NEW %s %s count=%d hand-coded %s (%s)" % (site[1], site[0], count,
                                                                               hand_codes, note))
            else:
                target_report.append("NEW-UNCODED %s %s count=%d" % (site[1], site[0], count))
        for site in sorted(set(entries) - set(il_target)):
            target_report.append("GONE %s %s (was %d)" % (site[1], site[0], entries[site]["count"]))
        entries = retargeted
        report.append("retarget %s: %d sites" % (args.retarget_label, len(il_target)))
    final_facts = target if args.retarget else facts
    handlers = {}
    for key, events in fws.vessel_event_handlers(final_facts).items():
        entry = {"events": events, "status": "exempt" if key in HANDLER_EXEMPT else "guard"}
        if key in HANDLER_EXEMPT:
            entry["reason"] = HANDLER_EXEMPT[key]
        handlers[key] = entry
    for key in sorted(set(HANDLER_EXEMPT) - set(handlers)):
        report.append("HANDLER EXEMPTION NAMES NO HANDLER %s" % key)
    print("\n".join(report))
    if target_report:
        print("--- %s against %s ---" % (args.retarget_label, args.commit))
        print("\n".join(target_report))
    if args.out:
        if diffs and not all(site in HAND_CODES for site in [(k, p) for k, p, _s, _i in diffs]):
            print("NOT WRITTEN: unresolved mismatches")
            return 1
        Path(args.out).write_text(fws.format_toml(
            entries, args.commit, args.retarget_label if args.retarget else None,
            not_wrappers=NOT_WRAPPERS, handlers=handlers), encoding="utf-8")
        print("wrote %s (%d entries)" % (args.out, len(entries)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
