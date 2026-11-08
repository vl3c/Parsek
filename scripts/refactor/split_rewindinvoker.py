#!/usr/bin/env python3
"""Split RewindInvoker.cs into partial-class files by responsibility.

A pure file move: every class-level member (method or nested type) is copied verbatim,
with its leading comments, doc comments and attributes, into
``RewindInvoker.<Area>.cs``; nothing inside a member is edited. The class (``internal static class RewindInvoker``)
gains the ``partial`` keyword and nothing else changes. Everything above the class
declaration (the usings, the namespace line and the class doc comment) stays in the
main file; each new file repeats only the usings. The source file starts with a UTF-8 BOM; the main file keeps it and the new files are
written without one.

State rule: C# runs field initializers (static AND instance) of a partial class in an order
that is undefined across its files, so EVERY field, event, property, constant, constructor
and any static constructor stays in the main file ``RewindInvoker.cs``. Only methods and nested
types may move. The script refuses a mapping that moves anything else.

Preprocessor rule: ``#region`` / ``#endregion`` lines at class level are not members. A
region whose members all land in ONE file travels with them (it wraps them in that file); a
region whose members are split across files keeps its two directive lines in the main file,
around whatever members of it stay there. Every output's regions are checked to pair up. Any
other preprocessor directive at class level fails the run; a directive INSIDE a member body
(a ``#pragma warning`` pair) is part of that member and travels with it.

Members are mapped by NAME (all overloads of a name travel together). Every method and
nested type must be named by an area or by MAIN_MEMBERS (an unknown new member fails the
run, so a rerun on a newer base never silently misfiles code). A mapped name that no
longer exists fails the run, so a stale mapping is noticed.

Before writing anything the script verifies that the multiset of non-blank lines across
the outputs equals the original's plus only the new files' headers / class wrappers and
the one ``class`` -> ``partial class`` declaration edit, that every output's braces balance
(each output is re-parsed), that the member count is preserved, and that no field-like
member landed outside the main file. Any mismatch fails loudly and nothing is written. A
second run over its own output is refused (the class is already partial), and a run over
the restored original writes byte-identical files.

Re-running on a newer base (for example after a merge conflict on the split branch):

    python3 scripts/refactor/split_rewindinvoker.py --remove-outputs
    git checkout <base> -- Source/Parsek/RewindInvoker.cs
    python3 scripts/refactor/split_rewindinvoker.py

(``--remove-outputs`` deletes exactly the AREAS files and nothing else.) Then map any
member it reports as unmapped (edit AREAS or MAIN_MEMBERS below) and run it again. The source gates that read this class by file read it through
Source/Parsek.Tests/RewindInvokerSourceFiles.cs (whole class) or name the one area file
that holds the method they scope to; harness/lib/ledgeraudit.py SRC_REWIND_INVOKER
names the file that holds RunStripActivateMarker. The ERS / ELS audit allowlist
(scripts/ers-els-audit-allowlist.txt) names every file that holds a raw
RecordingStore.CommittedRecordings / Ledger.Actions read, and the delete-family
pin (scripts/parsek-file-deletes-allowlist.txt) stays on the main file with
TryDeleteTemp, so a mapping change that moves either moves its entry too.
``--list`` prints every class-level member with its kind, line range and target file and
writes nothing; ``--check`` runs the split and verification without writing.

Plain stdlib, self-contained (no shared module with the other split scripts).
"""

import argparse
import bisect
import collections
import os
import re
import sys

REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
DEFAULT_SRC = os.path.join(REPO_ROOT, "Source", "Parsek", "RewindInvoker.cs")
CLASS_NAME = "RewindInvoker"
CLASS_DECL = "    internal static class RewindInvoker"
PARTIAL_DECL = "    internal static partial class RewindInvoker"

# Area -> (one-line description written above the class wrapper, member names in it).
# Order here is the order the files are reported in; members keep their original order
# inside each file.
AREAS = collections.OrderedDict([
    ("Gate", (
        "The invoke gate: CanInvoke and its evaluation, the future-rewind-point test and its "
        "clock read, the decision log line, the invokable-scene test and the cached "
        "precondition types (part loader).",
        [
            "CanInvoke", "EvaluateCanInvoke", "SlotRetireSetHoldsSealingRow",
            "IsRewindPointInFuture", "IsRewindPointInFutureNow", "ResolveNowUTForCanInvoke",
            "ReadPlanetariumUniversalTimeCore", "LogCanInvokeDecision", "GetRewindPointIdForLog",
            "FormatCanInvokePath", "FormatUtForCanInvokeLog", "FormatNullableBool",
            "SafeLoadedSceneForCanInvokeLog", "FormatActiveSessionForCanInvokeLog",
            "IsInvokableScene", "PreconditionResult", "PreconditionCache",
            "PartLoaderPrecondition",
        ])),
    ("ForkIdentity", (
        "Fork identity: the inherited identity copy onto the Re-Fly fork, its initial "
        "segment phase tag and the fork snapshot refresh from the live vessel.",
        [
            "CopyInheritedIdentityForFork", "TagForkInitialSegmentPhase",
            "TryRefreshForkSnapshotsFromLiveVessel", "CountSnapshotParts",
        ])),
    ("Advisory", (
        "The confirmation dialog and its Re-Fly advisory: sibling slot names, the advisory "
        "body, its log line and the capped name lists.",
        [
            "ShowDialog", "ResolveReFlySiblingSlotNames", "AddMappedSlotIndices",
            "ComposeReFlyAdvisoryBody", "LogReFlyAdvisory", "FormatReFlyAdvisoryLogLine",
            "FormatCappedProseList", "FormatCappedNameList",
        ])),
    ("PostLoad", (
        "The post-load phase: ConsumePostLoad, the abandoned restore attempt discard and the "
        "flight-ready probe, deferral and wait coroutine.",
        [
            "ConsumePostLoad", "DiscardRestoreAttemptOfAbandonedFlight", "IsFlightReady",
            "IsFlightReadyCore", "DeferUntilFlightReady", "WaitForFlightReadyAndInvoke",
        ])),
    ("StripActivate", (
        "Strip, activate and marker: the post-load strip / activate / marker run, the hold on "
        "resurrected vessels with a committed future, the post-strip spawn reconcile and the "
        "stage manager rebuild.",
        [
            "RunStripActivateMarker", "HoldResurrectedVesselsWithCommittedFuture",
            "BuildInvokeHoldExcludedPids", "ReconcilePostStripSpawnState",
            "ForceStageManagerRebuildAfterReFlyLoad",
        ])),
    ("SlotStrip", (
        "Disabled-slot strip and left-alone survey: the disabled sibling slot vessels to "
        "strip, the selected-slot presence test and the left-alone name collision warning.",
        [
            "StripDisabledSlotVessels", "ResolveDisabledSlotVesselsToStrip",
            "SelectedSlotVesselIsPresent", "WarnOnLeftAloneNameCollisions",
            "EmitStripLeftAloneWarn", "LeftAloneSurveyResult", "SurveyLiveLeftAloneCollisions",
            "ProjectLeftAloneNames", "SnapshotLiveVesselPids", "EnumerateCommittedVesselNames",
        ])),
    ("MarkerWrite", (
        "The atomic marker write: the provisional fork and its tree, the prior provisional "
        "reap for the rewind point, fork attach / detach and the recording lookups.",
        [
            "AtomicMarkerWrite", "FindTreeForReFlyFork", "ReapPriorProvisionalsForRp",
            "EnsureForkAttachedToTree", "DetachForkFromTreeForRollback",
            "BuildProvisionalRecording", "FindRecordingById", "SnapshotTreeBranchPointIds",
        ])),
    ("QuicksaveScrub", (
        "The rewind point quicksave: its save-root copy, the selected-slot scrub, sidecar "
        "epoch refresh, slot pid sets and affinity, and the forced throttle close.",
        [
            "ResolveAbsoluteQuicksavePath", "CopyQuicksaveToSaveRoot",
            "RequireSelectedSlotScrubApplied", "ReFlySaveScrubResult",
            "ScrubQuicksaveToSelectedSlotForReFly", "RefreshRecordingSidecarEpochsForTempSave",
            "RefreshRecordingSidecarEpochsInNode", "RefreshSingleRecordingSidecarEpoch",
            "StripSidecarHashes", "ReFlySlotAffinity", "ReFlySlotPidSets", "BuildSlotPidSets",
            "ClassifySlotAffinity", "BuildSelectedSlotPidSet", "BuildNonSelectedSlotPidSet",
            "TryResolveSelectedSlotRootPartPersistentId", "GetRootPartPersistentId", "ParseUInt",
            "ForceReFlyVesselThrottleClosed", "IsEngineModuleName",
            "SetOrAddValueAndReportChange", "SetExistingValueAndReportChange", "SetOrAddValue",
        ])),
    ("InPlaceDebris", (
        "In-place continuation debris: the pre-existing debris kill set, its protected pids "
        "and left-alone names, and the strip that removes it.",
        [
            "ResolveInPlaceContinuationDebrisToKill", "SnapshotKillTargets",
            "BuildProtectedPidsForInPlaceContinuation", "IsDebrisKillSurveyCandidate",
            "BuildLeftAlonePidNamesForInPlaceContinuation",
            "StripPreExistingDebrisForInPlaceContinuation",
        ])),
])

# Methods that stay in the main file on purpose: StartInvoke (the pre-load phase), the
# clock read, the temp quicksave delete, the user error and missing-quicksave handlers and
# the bundle restore. Listed so that "unmapped" reports only members a newer base added.
MAIN_MEMBERS = [
    "StartInvoke", "SafeNow", "TryDeleteTemp", "ShowUserError", "HandleQuicksaveMissing",
    "TryRestoreBundle",
]

MOVABLE_KINDS = ("method", "type")
REGION_RE = re.compile(r"^\s*#(region|endregion)\b")


class SplitError(Exception):
    pass


# ---------------------------------------------------------------------------
# Lexer: walks C# text, skipping comments and string / char literals, and reports
# the offsets where a class-level member ends.
# ---------------------------------------------------------------------------

def _skip_line_comment(text, i):
    j = text.find("\n", i)
    return len(text) if j < 0 else j


def _skip_block_comment(text, i):
    j = text.find("*/", i + 2)
    if j < 0:
        raise SplitError("unterminated block comment at offset %d" % i)
    return j + 2


def _skip_char(text, i):
    # text[i] == "'"
    j = i + 1
    while j < len(text):
        c = text[j]
        if c == "\\":
            j += 2
            continue
        if c == "'":
            return j + 1
        if c == "\n":
            raise SplitError("unterminated char literal at offset %d" % i)
        j += 1
    raise SplitError("unterminated char literal at offset %d" % i)


def _skip_string(text, i, verbatim, interpolated):
    """i is the offset of the opening quote. Returns the offset after the closing quote."""
    j = i + 1
    while j < len(text):
        c = text[j]
        if verbatim:
            if c == '"':
                if j + 1 < len(text) and text[j + 1] == '"':
                    j += 2
                    continue
                return j + 1
        else:
            if c == "\\":
                j += 2
                continue
            if c == '"':
                return j + 1
            if c == "\n":
                raise SplitError("unterminated string literal at offset %d" % i)
        if interpolated and c == "{":
            if j + 1 < len(text) and text[j + 1] == "{":
                j += 2
                continue
            j = _skip_code(text, j + 1, stop_at_close_brace=True)
            continue
        if interpolated and c == "}":
            if j + 1 < len(text) and text[j + 1] == "}":
                j += 2
                continue
        j += 1
    raise SplitError("unterminated string literal at offset %d" % i)


def _literal_start(text, i):
    """If a string literal starts at i, return (quote_offset, verbatim, interpolated)."""
    c = text[i]
    if c == '"':
        if text.startswith('"""', i):
            raise SplitError("raw string literal at offset %d is not supported" % i)
        return i, False, False
    if c in "@$":
        k = i
        verbatim = interpolated = False
        while k < len(text) and text[k] in "@$" and k - i < 3:
            if text[k] == "@":
                verbatim = True
            else:
                interpolated = True
            k += 1
        if k < len(text) and text[k] == '"' and (verbatim or interpolated):
            if text.startswith('"""', k):
                raise SplitError("raw string literal at offset %d is not supported" % k)
            return k, verbatim, interpolated
    return None


def _skip_code(text, i, stop_at_close_brace):
    """Skip code inside an interpolation hole; returns the offset after its closing brace."""
    depth = 0
    while i < len(text):
        c = text[i]
        if c == "/" and text.startswith("//", i):
            i = _skip_line_comment(text, i)
            continue
        if c == "/" and text.startswith("/*", i):
            i = _skip_block_comment(text, i)
            continue
        if c == "'":
            i = _skip_char(text, i)
            continue
        lit = _literal_start(text, i)
        if lit is not None:
            i = _skip_string(text, lit[0], lit[1], lit[2])
            continue
        if c == "{":
            depth += 1
        elif c == "}":
            if depth == 0 and stop_at_close_brace:
                return i + 1
            depth -= 1
        i += 1
    raise SplitError("unterminated interpolation hole")


def _next_significant(text, i):
    while i < len(text):
        c = text[i]
        if c.isspace():
            i += 1
            continue
        if text.startswith("//", i):
            i = _skip_line_comment(text, i)
            continue
        if text.startswith("/*", i):
            i = _skip_block_comment(text, i)
            continue
        return c
    return ""


def member_end_offsets(body):
    """Offsets (exclusive) at which each class-level member of ``body`` ends."""
    ends = []
    brace = paren = bracket = 0
    i = 0
    n = len(body)
    while i < n:
        c = body[i]
        if c == "/" and body.startswith("//", i):
            i = _skip_line_comment(body, i)
            continue
        if c == "/" and body.startswith("/*", i):
            i = _skip_block_comment(body, i)
            continue
        if c == "'":
            i = _skip_char(body, i)
            continue
        lit = _literal_start(body, i)
        if lit is not None:
            i = _skip_string(body, lit[0], lit[1], lit[2])
            continue
        if c == "#" and brace == 0 and (i == 0 or body[i - 1] == "\n" or body[:i].split("\n")[-1].strip() == ""):
            raise SplitError("preprocessor directive at class level is not supported (offset %d)" % i)
        if c == "{":
            brace += 1
        elif c == "}":
            brace -= 1
            if brace < 0:
                raise SplitError("unbalanced '}' at offset %d" % i)
            if brace == 0 and paren == 0 and bracket == 0:
                nxt = _next_significant(body, i + 1)
                if nxt not in ("=", ";", ".", ",", ")"):
                    ends.append(i + 1)
        elif c == "(":
            paren += 1
        elif c == ")":
            paren -= 1
        elif c == "[":
            bracket += 1
        elif c == "]":
            bracket -= 1
        elif c == ";" and brace == 0 and paren == 0 and bracket == 0:
            ends.append(i + 1)
        i += 1
    if brace or paren or bracket:
        raise SplitError("unbalanced class body (brace=%d paren=%d bracket=%d)" % (brace, paren, bracket))
    return ends


def mask_comments_and_literals(text):
    """Comments removed, string / char literal contents replaced by placeholders."""
    out = []
    i = 0
    n = len(text)
    while i < n:
        c = text[i]
        if c == "/" and text.startswith("//", i):
            i = _skip_line_comment(text, i)
            continue
        if c == "/" and text.startswith("/*", i):
            i = _skip_block_comment(text, i)
            out.append(" ")
            continue
        if c == "'":
            i = _skip_char(text, i)
            out.append("'c'")
            continue
        lit = _literal_start(text, i)
        if lit is not None:
            i = _skip_string(text, lit[0], lit[1], lit[2])
            out.append('"s"')
            continue
        out.append(c)
        i += 1
    return "".join(out)


# ---------------------------------------------------------------------------
# Member model
# ---------------------------------------------------------------------------

Member = collections.namedtuple("Member", "name kind first_line last_line lines")
# kind: "method" | "type" | "const" | "field" | "property" | "constructor"
#       | "region" | "endregion" (a directive line plus the blank lines before it)

_TYPE_RE = re.compile(r"\b(class|struct|enum|interface|record)\s+(\w+)")
_IDENT_BEFORE_RE = re.compile(r"(\w+)\s*(?:<[^<>]*>)?\s*$")


def _strip_leading_attributes(decl):
    decl = decl.lstrip()
    while decl.startswith("["):
        depth = 0
        for k, ch in enumerate(decl):
            if ch == "[":
                depth += 1
            elif ch == "]":
                depth -= 1
                if depth == 0:
                    decl = decl[k + 1:].lstrip()
                    break
        else:
            raise SplitError("unbalanced attribute in: %r" % decl[:120])
    return decl


_MODIFIER_WORDS = {"public", "private", "internal", "protected", "static", "readonly",
                   "const", "unsafe", "extern", "new", "override", "virtual", "sealed",
                   "abstract", "partial", "volatile", "async", "ref"}


def _first_stop(decl):
    """Offset of the first '(' / '=' / ';' / '{' that ends a declaration head.

    Skips generic argument lists (``Dictionary<int, (double a, double b)>``) and tuple
    types (``private (int, int) Foo(...)``): a '(' is a parameter list only when it
    follows an identifier that is not a modifier, or a generic method's '>'.
    """
    angle = 0
    i = 0
    n = len(decl)
    while i < n:
        c = decl[i]
        if c == "<":
            angle += 1
        elif c == ">" and angle > 0:
            angle -= 1
        elif angle == 0 and c in "=;{":
            return i
        elif c == "(":
            prev = decl[:i].rstrip()
            word = re.search(r"(\w+)$", prev)
            is_params = angle == 0 and ((word is not None and word.group(1) not in _MODIFIER_WORDS)
                                        or prev.endswith(">"))
            if is_params:
                return i
            depth = 0
            while i < n:
                if decl[i] == "(":
                    depth += 1
                elif decl[i] == ")":
                    depth -= 1
                    if depth == 0:
                        break
                i += 1
        i += 1
    return n


def classify_member(chunk_text):
    decl = _strip_leading_attributes(mask_comments_and_literals(chunk_text))
    if not decl.strip():
        raise SplitError("empty member chunk")
    first = _first_stop(decl)
    tm = _TYPE_RE.search(decl)
    if tm and tm.start() < first:
        return tm.group(2), "type"
    head = decl[:first]
    stop_char = decl[first] if first < len(decl) else ""
    m = _IDENT_BEFORE_RE.search(head)
    if not m:
        raise SplitError("cannot name member: %r" % decl[:160])
    name = m.group(1)
    if re.search(r"\bevent\b", head):
        return name, "field"
    if stop_char == "(":
        if name == CLASS_NAME:
            return name, "constructor"
        return name, "method"
    if stop_char in ("=", ";"):
        if re.search(r"\bconst\b", head):
            return name, "const"
        return name, "field"
    if stop_char == "{":
        return name, "property"
    raise SplitError("cannot classify member: %r" % decl[:160])


def _split_regions(body_lines, start, end):
    """Split body lines [start, end] at region directive lines.

    Returns (pseudo_members, rest_start): one region / endregion pseudo-member per
    directive (owning the blank lines before it), and the first line after the last
    directive. Only blank lines may precede a directive inside a chunk.
    """
    out = []
    cur = start
    for k in range(start, end + 1):
        m = REGION_RE.match(body_lines[k])
        if not m:
            continue
        for j in range(cur, k):
            if body_lines[j].strip():
                raise SplitError("non-blank line %r before a region directive on body line %d"
                                 % (body_lines[j], k + 1))
        out.append(Member(body_lines[k].strip(), m.group(1), cur, k, body_lines[cur:k + 1]))
        cur = k + 1
    return out, cur


def parse_file(text, decl=CLASS_DECL):
    lines = text.split("\n")
    trailing_newline = text.endswith("\n")
    if trailing_newline:
        lines = lines[:-1]
    decl_idx = [k for k, l in enumerate(lines) if l == decl]
    if len(decl_idx) != 1:
        if decl == CLASS_DECL and any(l == PARTIAL_DECL for l in lines):
            raise SplitError("%s is already partial; restore the unsplit file first (see docstring)"
                             % CLASS_NAME)
        raise SplitError("expected exactly one line %r, found %d" % (decl, len(decl_idx)))
    d = decl_idx[0]
    if lines[d + 1] != "    {":
        raise SplitError("expected '    {' after the class declaration")
    if lines[-2:] != ["    }", "}"]:
        raise SplitError("expected the file to end with '    }' and '}'")
    header = lines[:d + 2]
    footer = lines[-2:]
    body_lines = lines[d + 2:-2]
    ns_idx = [k for k, l in enumerate(header) if l.startswith("namespace ")]
    if len(ns_idx) != 1:
        raise SplitError("expected one namespace line in the header")
    usings = header[:ns_idx[0]]
    namespace_line = header[ns_idx[0]]
    if header[ns_idx[0] + 1] != "{":
        raise SplitError("expected '{' after the namespace line")

    # Region directive lines are blanked (same length, so offsets hold) for the lexer.
    lex_lines = [(" " * len(l)) if REGION_RE.match(l) else l for l in body_lines]
    body = "\n".join(lex_lines) + "\n"
    ends = member_end_offsets(body)
    starts = [0]
    for k, ch in enumerate(body):
        if ch == "\n":
            starts.append(k + 1)
    members = []
    prev_line = 0  # first body line index not yet assigned
    for off in ends:
        line_idx = bisect.bisect_right(starts, off - 1) - 1
        rest = body[off:starts[line_idx + 1] - 1] if line_idx + 1 < len(starts) else body[off:]
        rest_s = rest.strip()
        if rest_s and not rest_s.startswith("//"):
            raise SplitError("code after a member end on body line %d: %r"
                             % (line_idx + 1, body_lines[line_idx]))
        if line_idx < prev_line:
            raise SplitError("two members end on body line %d: %r" % (line_idx + 1, body_lines[line_idx]))
        regions, chunk_start = _split_regions(body_lines, prev_line, line_idx)
        members.extend(regions)
        chunk = body_lines[chunk_start:line_idx + 1]
        name, kind = classify_member("\n".join(chunk))
        members.append(Member(name, kind, chunk_start, line_idx, chunk))
        prev_line = line_idx + 1
    regions, tail_start = _split_regions(body_lines, prev_line, len(body_lines) - 1)
    members.extend(regions)
    tail = body_lines[tail_start:]
    if mask_comments_and_literals("\n".join(tail)).strip():
        raise SplitError("unparsed code after the last member: %r" % tail[:5])
    check_regions(members, "input" if decl == CLASS_DECL else "output")
    return {
        "lines": lines,
        "trailing_newline": trailing_newline,
        "header": header,
        "footer": footer,
        "usings": usings,
        "namespace_line": namespace_line,
        "decl_index": d,
        "members": members,
        "tail": tail,
        "body_offset": d + 2,
    }


def check_regions(members, where):
    depth = 0
    for m in members:
        if m.kind == "region":
            depth += 1
            if depth > 1:
                raise SplitError("%s: nested #region at %r is not supported" % (where, m.name))
        elif m.kind == "endregion":
            depth -= 1
            if depth < 0:
                raise SplitError("%s: #endregion without a #region" % where)
    if depth:
        raise SplitError("%s: unclosed #region" % where)


# ---------------------------------------------------------------------------
# Split
# ---------------------------------------------------------------------------

def assign(members):
    name_to_area = {}
    for area, (_, names) in AREAS.items():
        for nm in names:
            if nm in name_to_area:
                raise SplitError("member %s mapped twice (%s and %s)" % (nm, name_to_area[nm], area))
            name_to_area[nm] = area
    present = set(m.name for m in members if m.kind not in ("region", "endregion"))
    for nm in MAIN_MEMBERS:
        if nm in name_to_area:
            raise SplitError("member %s is both in MAIN_MEMBERS and mapped to %s" % (nm, name_to_area[nm]))
    stale = sorted(n for n in list(name_to_area) + MAIN_MEMBERS if n not in present)
    if stale:
        raise SplitError("mapping names members that no longer exist: %s" % ", ".join(stale))
    targets = []
    unmapped = []
    for m in members:
        if m.kind in ("region", "endregion"):
            targets.append(None)  # decided below
            continue
        area = name_to_area.get(m.name)
        if area is not None and m.kind not in MOVABLE_KINDS:
            raise SplitError("%s %s is mapped to %s, but state (%s) must stay in the main file"
                             % (m.kind, m.name, area, m.kind))
        if area is None and m.kind in MOVABLE_KINDS and m.name not in MAIN_MEMBERS:
            unmapped.append("%s %s (body line %d)" % (m.kind, m.name, m.first_line + 1))
        targets.append(area)
    # A region travels with its members only when they all land in one file.
    open_idx = None
    for idx, m in enumerate(members):
        if m.kind == "region":
            open_idx = idx
        elif m.kind == "endregion":
            inner = set(targets[open_idx + 1:idx])
            if len(inner) == 1:
                dest = inner.pop()
                targets[open_idx] = dest
                targets[idx] = dest
            open_idx = None
    return targets, unmapped


def wrapper_lines(parsed, area):
    desc = AREAS[area][0]
    head = list(parsed["usings"])
    head += [parsed["namespace_line"], "{", "    // " + desc, PARTIAL_DECL, "    {"]
    return head, ["    }", "}"]


def build_outputs(parsed, targets):
    members = parsed["members"]
    per_file = collections.OrderedDict()
    per_file[None] = []
    for area in AREAS:
        per_file[area] = []
    last_index = {}
    for idx, (m, area) in enumerate(zip(members, targets)):
        chunk = list(m.lines)
        out = per_file[area]
        if not out:
            if area is not None:
                while chunk and not chunk[0].strip():
                    chunk = chunk[1:]
        elif last_index.get(area) != idx - 1 and chunk and chunk[0].strip() and out[-1].strip():
            # Not adjacent in the original: keep one blank line between members.
            out.append("")
        out.extend(chunk)
        last_index[area] = idx
    outputs = collections.OrderedDict()
    header = list(parsed["header"])
    header[parsed["decl_index"]] = PARTIAL_DECL  # the one declaration edit: add `partial`
    outputs[None] = header + per_file[None] + parsed["tail"] + parsed["footer"]
    for area in AREAS:
        if not per_file[area]:
            raise SplitError("area %s received no members" % area)
        head, foot = wrapper_lines(parsed, area)
        outputs[area] = head + per_file[area] + foot
    return outputs


def verify(parsed, outputs):
    def nonblank(lines):
        return collections.Counter(l for l in lines if l.strip())

    expected = nonblank(parsed["lines"])
    expected[CLASS_DECL] -= 1
    if expected[CLASS_DECL] == 0:
        del expected[CLASS_DECL]
    expected[PARTIAL_DECL] += 1
    for area in outputs:
        if area is None:
            continue
        head, foot = wrapper_lines(parsed, area)
        expected.update(nonblank(head + foot))
    actual = collections.Counter()
    for lines in outputs.values():
        actual.update(nonblank(lines))
    if actual != expected:
        missing = expected - actual
        extra = actual - expected
        msg = ["non-blank line multiset mismatch"]
        for l, c in list(missing.items())[:20]:
            msg.append("  missing x%d: %r" % (c, l))
        for l, c in list(extra.items())[:20]:
            msg.append("  extra   x%d: %r" % (c, l))
        raise SplitError("\n".join(msg))
    # Each output re-parses (braces balance, regions pair up) and, outside the main file,
    # holds no field-like member.
    total = 0
    for area, lines in outputs.items():
        sub = parse_file("\n".join(lines) + "\n", decl=PARTIAL_DECL)
        for m in sub["members"]:
            if m.kind in ("region", "endregion"):
                continue
            total += 1
            if area is not None and m.kind not in MOVABLE_KINDS:
                raise SplitError("verification: %s %s landed in %s" % (m.kind, m.name, area))
    original = sum(1 for m in parsed["members"] if m.kind not in ("region", "endregion"))
    if total != original:
        raise SplitError("verification: member count %d != original %d" % (total, original))


def out_path(src, area):
    if area is None:
        return src
    base = os.path.splitext(src)[0]
    return "%s.%s.cs" % (base, area)


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--src", default=DEFAULT_SRC, help="the unsplit RewindInvoker.cs (default: %(default)s)")
    ap.add_argument("--list", action="store_true", help="print the member table and exit")
    ap.add_argument("--check", action="store_true", help="split and verify, write nothing")
    ap.add_argument("--remove-outputs", action="store_true",
                    help="delete the AREAS partial files (only those) and exit")
    args = ap.parse_args(argv)

    src = os.path.abspath(args.src)
    if args.remove_outputs:
        for area in AREAS:
            path = out_path(src, area)
            if os.path.exists(path):
                os.remove(path)
                print("removed %s" % os.path.relpath(path, REPO_ROOT))
        return 0
    if not (args.check or args.list):
        existing = [out_path(src, a) for a in AREAS if os.path.exists(out_path(src, a))]
        if existing:
            raise SplitError("partial files already exist; remove them first (--remove-outputs): %s"
                             % ", ".join(existing))
    with open(src, "r", encoding="utf-8", newline="") as fh:
        text = fh.read()
    if "\r" in text:
        raise SplitError("CRLF line endings are not supported; normalize first")
    # A leading BOM stays on the main file only; the new partial files are written without one.
    bom = text.startswith("\ufeff")
    if bom:
        text = text[1:]
    parsed = parse_file(text)
    targets, unmapped = assign(parsed["members"])
    if args.list:
        off = parsed["body_offset"] + 1
        for m, area in zip(parsed["members"], targets):
            print("%5d-%5d %5d  %-11s %-18s %s" % (m.first_line + off, m.last_line + off,
                                                   m.last_line - m.first_line + 1, m.kind,
                                                   area or "(main)", m.name))
        return 0
    if unmapped:
        raise SplitError("unmapped members (add each to AREAS or MAIN_MEMBERS):\n  "
                         + "\n  ".join(unmapped))
    outputs = build_outputs(parsed, targets)
    verify(parsed, outputs)
    for area, lines in outputs.items():
        path = out_path(src, area)
        content = "\n".join(lines) + ("\n" if parsed["trailing_newline"] else "")
        if bom and area is None:
            content = "\ufeff" + content
        print("%-60s %6d lines" % (os.path.relpath(path, REPO_ROOT), len(lines)))
        if not args.check:
            with open(path, "w", encoding="utf-8", newline="") as fh:
                fh.write(content)
    print("verified: non-blank line multiset preserved; braces and regions balance; "
          "every field, property, constant and constructor stays in the main file")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except SplitError as exc:
        sys.stderr.write("split_rewindinvoker: FAILED: %s\n" % exc)
        sys.exit(1)
