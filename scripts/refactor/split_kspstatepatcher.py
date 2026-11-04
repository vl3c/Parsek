#!/usr/bin/env python3
"""Split GameActions/KspStatePatcher.cs into partial-class files, one per stock state family.

A pure file move: every class-level member (method or nested type) is copied verbatim,
with its leading comments, doc comments and attributes, into
``KspStatePatcher.<Family>.cs``; nothing inside a member is edited. The class gains the
``partial`` keyword and nothing else changes.

Static state rule: C# runs the static field initializers of a partial class in an
undefined order across its files, so EVERY static field (and any property or static
constructor) stays in the main file ``KspStatePatcher.cs``. Only methods, nested types and
``const`` declarations (compile-time literals, no runtime initializer) may move. The script
refuses a mapping that moves anything else.

Members are mapped by NAME (all overloads of a name travel together). A member the
mapping does not name stays in the main file and is reported. A mapped name that no
longer exists fails the run, so a stale mapping is noticed.

Before writing anything the script verifies that the multiset of non-blank lines across
the outputs equals the original's, plus only the new files' headers / class wrappers and
the one ``static class`` -> ``static partial class`` declaration edit, and that every
output's braces balance. Any mismatch fails loudly and nothing is written.

Re-running on a newer main (for example after a merge conflict on the split branch):

    git checkout origin/main -- Source/Parsek/GameActions/KspStatePatcher.cs
    rm -f Source/Parsek/GameActions/KspStatePatcher.*.cs
    python3 scripts/refactor/split_kspstatepatcher.py

then map any member it reports as unmapped (edit FAMILIES below) and run it again.
``--list`` prints every class-level member with its kind, line range and target file and
writes nothing; ``--check`` runs the split and verification without writing.

Plain stdlib, self-contained (no shared module with the other split scripts).
"""

import argparse
import collections
import os
import re
import sys

REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
DEFAULT_SRC = os.path.join(REPO_ROOT, "Source", "Parsek", "GameActions", "KspStatePatcher.cs")
CLASS_NAME = "KspStatePatcher"
CLASS_DECL = "    internal static class KspStatePatcher"
PARTIAL_DECL = "    internal static partial class KspStatePatcher"

# Family -> (one-line description written above the class wrapper, member names in it).
# Order here is the order the files are reported in; members keep their original order
# inside each file.
FAMILIES = collections.OrderedDict([
    ("Science", (
        "Science pool patch, its pending-adjustment folds, and per-subject science.",
        [
            "SciencePoolPatchDecision", "ResolveSciencePoolPatch", "PatchScience",
            "AdjustSciencePatchTargetForPendingRecentTechResearch",
            "AdjustSciencePatchTargetForPendingRecentScienceEarning",
            "AdjustSciencePatchTargetForPendingStrategyScienceDebit",
            "ComputePendingAdjustedRunningScience",
            "ComputePreStrategyPendingAdjustedRunningScience",
            "SubjectSciencePatchDecision", "ResolveSubjectSciencePatch",
            "MissingSubjectCreationDecision", "ResolveMissingSubjectCreation",
            "GetScienceSubjectsDictionary", "EmitScienceSubjectsReflectionWarnOnce",
            "TryCreateAndRegisterSubject", "PatchPerSubjectScience", "BuildSubjectIdsForPatch",
        ])),
    ("Tech", (
        "R&D tech tree patch and the committed tech-unlock pass.",
        [
            "BuildTargetTechIdsForPatch", "UnaffordableUnlockDrop", "IsRefusedUnaffordableUnlockRow",
            "ShouldRefuseUnaffordableRelock", "TechRelockOutcome", "ResolveTechRelockOutcome",
            "GetSelectedTechBaselineUt", "SelectTechBaselineForPatch", "TechNodePatchAction",
            "ClassifyTechNodeForPatch", "PatchTechTree", "EnsureAvailableProtoTechNode",
            "AddPurchasedPartsForTech", "AddPurchasedParts", "ContainsPurchasedPart",
            "GetProtoTechNodesDictionary", "EmitProtoTechNodesReflectionWarnOnce",
            "RefreshTechTreeUi",
            "CommittedTechUnlockSkipTechCutoff", "CommittedTechUnlockSkipClockNotReady",
            "CommittedTechUnlockSkipRewindPending",
            "BuildCommittedTechUnlockIdsForPatch", "PlanCommittedTechUnlocksForPatch",
            "PatchCommittedTechUnlocks",
        ])),
    ("Unlocks", (
        "Add-only purchase-state unlocks: part purchases, resource scans and part upgrades.",
        [
            "ResolvePartPurchasePatchCutoff", "IsFiniteExplicitCutoff",
            "BuildPurchasedPartNamesForPatch", "PlanPurchasedPartNamesForPatch",
            "PartPurchasePatchOutcome", "PartPurchasePatchResult", "ApplyPartPurchasePatch",
            "ClassifyPartPurchaseForPatch", "PatchPurchasedParts", "RefreshEditorPartList",
            "ResourceScanUnlock", "BuildResourceScanUnlocksForPatch",
            "PlanResourceScanUnlocksForPatch", "ResourceScanPatchResult",
            "ApplyResourceScanUnlocks", "PatchResourceScans", "IsResourceScanStateLoaded",
            "ApplyLiveResourceScanUnlocks", "MirrorCommittedResourceScans",
            "BuildUnlockedUpgradeNamesForPatch", "PlanUnlockedUpgradeNamesForPatch",
            "UpgradeUnlockPatchOutcome", "UpgradeUnlockPatchResult",
            "ClassifyUpgradeUnlockForPatch", "ApplyUpgradeUnlockPatch",
            "PatchUnlockedUpgrades", "ReadLiveUpgradeStateSkipReason", "ApplyLiveUpgradeUnlocks",
        ])),
    ("FundsReputation", (
        "Funds and reputation pool patches.",
        [
            "FundsPatchDecision", "ResolveFundsPatch", "PatchFunds",
            "ReputationPatchDecision", "ResolveReputationPatch", "PatchReputation",
        ])),
    ("PoolGuards", (
        "Shared pool policy for the funds / science / reputation patches: the negative-currency "
        "target, the drawdown guard and the stock pool reference.",
        [
            "ReadAllowNegativeCurrency", "ReadLiveAllowNegativeCurrency",
            "ResolveNegativeCurrencyPatchTarget",
            "ResolveFundsPatchTarget", "ResolveSciencePatchBaseTarget",
            "IsSuspiciousDrawdown", "ResetDrawdownGuardSessionLatches",
            "DrawdownGuardSessionToastLatch", "ClampDirection", "IsGuardableDrawdown",
            "IsGuardableUplift", "ClockCrossing", "MeasureStockPool", "EmitTakeBacks",
            "EmitTakeBackShortfall", "NoteScienceTakeBackShortfall", "FileStockPoolAdjustment",
            "ApplyCarryDedupe", "CommitStockPool", "EmitStockPoolLeak",
            "IsLiveUnchangedSinceLastPatch", "ResolveClockCrossingAuthorization",
            "ApplyDrawdownGuard", "EmitDrawdownGuardClamp",
        ])),
    ("RewindReadback", (
        "Rewind read-back divergence guard run at the top of PatchAll.",
        [
            "RewindReadbackVerdict", "RewindReadbackFundsTolerance",
            "RewindReadbackScienceTolerance", "RewindReadbackReputationTolerance",
            "ResolveRewindDivergence", "IsFinite",
            "RunRewindReadbackGuard", "EvaluateReadbackResource", "RewindReadbackTag",
            "FmtNullable",
        ])),
    ("Milestones", (
        "Milestone and progress-tree patch, including repeatable record nodes.",
        [
            "PatchMilestones", "LegacyBareIdBodyName", "IsLegacyBareIdSubtree",
            "PatchProgressNodeTree", "RepeatableRecordState", "TryComputeRepeatableRecordState",
            "MaxRepeatableRecordBands", "TryComputeRepeatableRecordImpliedPaidCount",
            "TryReadRepeatableRecordImpliedPaidCount", "ObserveProgressTreeForSeed",
            "ObserveProgressTreeForSeedCore", "PatchRepeatableRecordNode",
            "TryRaiseRepeatableRecordBand", "IsRepeatableRecordType",
            "NormalizeRepeatableRecordValue", "TryGetRepeatableRecordDefinition",
            "CreateRepeatableRecordIterator",
        ])),
    ("Contracts", (
        "Contract list patch: entry point, stock-load deferral, twin withdrawal, the core pass and its clocks.",
        [
            "PatchContracts", "ContractPatchTiming", "DecideContractPatchTiming",
            "DeferredContractPatch", "ResolveDeferredContractClockUT",
            "DeferContractPatchUntilStockLoaded", "OnStockContractsLoaded",
            "ResetDeferredContractPatch", "WithdrawOfferedContractTwins", "PatchContractsCore",
            "ContractAcceptClockToleranceSeconds", "IsRealClock",
            "ResolveContractAcceptClockUT", "SelectContractsAcceptedByNow",
        ])),
    ("ContractParameters", (
        "Contract parameter completion mirror and the stock contract re-evaluation.",
        [
            "StockContractParameterState",
            "ContractParameterPatchDecision", "ResolveContractParameterClockUT",
            "DecideContractParameterPatch", "ContractParameterPatchPlan",
            "PlanContractParameterPatch", "ParseContractParameterIndexPath",
            "ResolveStockContractParameter", "TryCompleteStockParameterSilently",
            "EmitContractParameterStateReflectionWarnOnce", "IndexActiveContracts",
            "CompleteCreditedParametersSilently", "MirrorCommittedContractParameters",
            "ReadLiveContractsOrNull",
            "PatchContractParameters", "ContractCompletionShape", "ReadContractCompletionShape",
            "CollectContractsCompletedAfter", "NeedsStockContractReEvaluation",
            "RunContractReEvaluation",
        ])),
    ("ContractRemoval", (
        "Contract partition, removal keys and the silent retire / archive of finished contracts.",
        [
            "ContractFilterEntry", "ContractRemovalKey", "PartitionContractsForPatch",
            "PartitionFinishedContractsForPatch", "BuildCurrentContractRemovalKeys",
            "BuildEndedLifecycleOfferedRemovalKeys", "BuildFinishedContractRemovalKeys",
            "ToRemovalKey", "RetiredContractMove", "TryMapOutcomeToFinishedState",
            "PlanRetiredActiveContractMoves", "TryRetireContractSilently",
            "EmitContractStateReflectionWarnOnce", "ArchiveRetiredActiveContracts",
            "ComposeBoundedIdentitySample", "DescribeRemovedContractIdentities",
            "IsSurvivingTerminalEntry", "IsTerminalContractStateCompatible",
            "BuildContractFilterEntries", "BuildTerminalContractOutcomeMap",
            "BuildTerminalContractUtMap", "RemoveContractsByKey", "IsOfferedCopyOfRestoredContract",
            "RemoveOfferedCopiesOfContract", "IsTerminalContractState",
        ])),
])

# Methods that stay in the main file on purpose (the PatchAll facade, its order and
# suppression scope, the shared log helpers and the resets). Listed so that "unmapped"
# reports only members a newer main added.
MAIN_MEMBERS = [
    "VerboseStablePatchState", "EmitPoolReadbackAnomaly", "PatchAll", "PatchPools",
    "ShouldHoldPurchaseStatePatches", "PatchFacilities", "PatchStrategies",
    "ResetForSaveLoad", "ResetForTesting",
]

MOVABLE_KINDS = ("method", "type", "const")


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
        if c == "#" and (i == 0 or body[i - 1] == "\n" or body[:i].split("\n")[-1].strip() == ""):
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


def classify_member(chunk_text):
    decl = _strip_leading_attributes(mask_comments_and_literals(chunk_text))
    if not decl.strip():
        raise SplitError("empty member chunk")
    stops = [p for p in (decl.find(ch) for ch in "(=;{") if p >= 0]
    first = min(stops) if stops else len(decl)
    tm = _TYPE_RE.search(decl)
    if tm and tm.start() < first:
        return tm.group(2), "type"
    head = decl[:first]
    stop_char = decl[first] if first < len(decl) else ""
    m = _IDENT_BEFORE_RE.search(head)
    if not m:
        raise SplitError("cannot name member: %r" % decl[:160])
    name = m.group(1)
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


def parse_file(text):
    lines = text.split("\n")
    trailing_newline = text.endswith("\n")
    if trailing_newline:
        lines = lines[:-1]
    decl_idx = [k for k, l in enumerate(lines) if l == CLASS_DECL]
    if len(decl_idx) != 1:
        if any(l == PARTIAL_DECL for l in lines):
            raise SplitError("%s is already partial; restore the unsplit file first (see docstring)"
                             % CLASS_NAME)
        raise SplitError("expected exactly one line %r, found %d" % (CLASS_DECL, len(decl_idx)))
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

    body = "\n".join(body_lines) + "\n"
    ends = member_end_offsets(body)
    # Line start offsets of the body.
    starts = [0]
    for k, ch in enumerate(body):
        if ch == "\n":
            starts.append(k + 1)
    import bisect
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
            # A second end on a line already closed (e.g. `int a; int b;` on one line).
            raise SplitError("two members end on body line %d: %r" % (line_idx + 1, body_lines[line_idx]))
        chunk = body_lines[prev_line:line_idx + 1]
        name, kind = classify_member("\n".join(chunk))
        members.append(Member(name, kind, prev_line, line_idx, chunk))
        prev_line = line_idx + 1
    tail = body_lines[prev_line:]
    if mask_comments_and_literals("\n".join(tail)).strip():
        raise SplitError("unparsed code after the last member: %r" % tail[:5])
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


# ---------------------------------------------------------------------------
# Split
# ---------------------------------------------------------------------------

def assign(members):
    name_to_family = {}
    for fam, (_, names) in FAMILIES.items():
        for nm in names:
            if nm in name_to_family:
                raise SplitError("member %s mapped twice (%s and %s)" % (nm, name_to_family[nm], fam))
            name_to_family[nm] = fam
    present = set(m.name for m in members)
    for nm in MAIN_MEMBERS:
        if nm in name_to_family:
            raise SplitError("member %s is both in MAIN_MEMBERS and mapped to %s" % (nm, name_to_family[nm]))
    stale = sorted(n for n in list(name_to_family) + MAIN_MEMBERS if n not in present)
    if stale:
        raise SplitError("mapping names members that no longer exist: %s" % ", ".join(stale))
    targets = []
    unmapped = []
    for m in members:
        fam = name_to_family.get(m.name)
        if fam is not None and m.kind not in MOVABLE_KINDS:
            raise SplitError("%s %s is mapped to %s, but static state (%s) must stay in the main file"
                             % (m.kind, m.name, fam, m.kind))
        if fam is None and m.kind in ("method", "type") and m.name not in MAIN_MEMBERS:
            unmapped.append(m.name)
        targets.append(fam)
    return targets, unmapped


def wrapper_lines(parsed, family):
    desc = FAMILIES[family][0]
    head = list(parsed["usings"])
    head += [parsed["namespace_line"], "{", "    // " + desc, PARTIAL_DECL, "    {"]
    return head, ["    }", "}"]


def build_outputs(parsed, targets):
    members = parsed["members"]
    per_file = collections.OrderedDict()
    per_file[None] = []
    for fam in FAMILIES:
        per_file[fam] = []
    last_index = {}
    for idx, (m, fam) in enumerate(zip(members, targets)):
        chunk = list(m.lines)
        out = per_file[fam]
        if not out:
            if fam is not None:
                while chunk and not chunk[0].strip():
                    chunk = chunk[1:]
        elif last_index.get(fam) != idx - 1 and chunk and chunk[0].strip() and out[-1].strip():
            # Not adjacent in the original: keep one blank line between members.
            out.append("")
        out.extend(chunk)
        last_index[fam] = idx
    outputs = collections.OrderedDict()
    main = list(parsed["header"])
    main[parsed["decl_index"]] = PARTIAL_DECL
    main += per_file[None] + parsed["tail"] + parsed["footer"]
    outputs[None] = main
    for fam in FAMILIES:
        if not per_file[fam]:
            continue
        head, foot = wrapper_lines(parsed, fam)
        outputs[fam] = head + per_file[fam] + foot
    return outputs


def verify(parsed, outputs):
    def nonblank(lines):
        return collections.Counter(l for l in lines if l.strip())

    expected = nonblank(parsed["lines"])
    expected[CLASS_DECL] -= 1
    if expected[CLASS_DECL] == 0:
        del expected[CLASS_DECL]
    expected[PARTIAL_DECL] += 1
    for fam in outputs:
        if fam is None:
            continue
        head, foot = wrapper_lines(parsed, fam)
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
    # Each output must balance and, outside the main file, hold no static state.
    for fam, lines in outputs.items():
        text = "\n".join(lines) + "\n"
        sub = parse_file(text.replace(PARTIAL_DECL, CLASS_DECL, 1))
        for m in sub["members"]:
            if fam is not None and m.kind not in MOVABLE_KINDS:
                raise SplitError("verification: %s %s landed in %s" % (m.kind, m.name, fam))
    # Every original member is in exactly one output, in original order.
    total = sum(len(parse_file("\n".join(l).replace(PARTIAL_DECL, CLASS_DECL, 1) + "\n")["members"])
                for l in outputs.values())
    if total != len(parsed["members"]):
        raise SplitError("verification: member count %d != original %d" % (total, len(parsed["members"])))


def out_path(src, family):
    if family is None:
        return src
    base = os.path.splitext(src)[0]
    return "%s.%s.cs" % (base, family)


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--src", default=DEFAULT_SRC, help="the unsplit KspStatePatcher.cs (default: %(default)s)")
    ap.add_argument("--list", action="store_true", help="print the member table and exit")
    ap.add_argument("--check", action="store_true", help="split and verify, write nothing")
    args = ap.parse_args(argv)

    src = os.path.abspath(args.src)
    with open(src, "r", encoding="utf-8", newline="") as fh:
        text = fh.read()
    if "\r" in text:
        raise SplitError("CRLF line endings are not supported; normalize first")
    if text.startswith("﻿"):
        raise SplitError("a BOM is not supported")
    parsed = parse_file(text)
    targets, unmapped = assign(parsed["members"])
    if args.list:
        off = parsed["body_offset"] + 1
        for m, fam in zip(parsed["members"], targets):
            print("%5d-%5d  %-11s %-16s %s" % (m.first_line + off, m.last_line + off, m.kind,
                                               fam or "(main)", m.name))
        return 0
    if not args.check:
        existing = [out_path(src, f) for f in FAMILIES if os.path.exists(out_path(src, f))]
        if existing:
            raise SplitError("partial files already exist; remove them first: %s" % ", ".join(existing))
    outputs = build_outputs(parsed, targets)
    verify(parsed, outputs)
    for fam, lines in outputs.items():
        path = out_path(src, fam)
        content = "\n".join(lines) + ("\n" if parsed["trailing_newline"] else "")
        print("%-60s %6d lines" % (os.path.relpath(path, REPO_ROOT), len(lines)))
        if not args.check:
            with open(path, "w", encoding="utf-8", newline="") as fh:
                fh.write(content)
    if unmapped:
        print("unmapped members kept in the main file: %s" % ", ".join(sorted(set(unmapped))))
    print("verified: non-blank line multiset preserved; static fields all in the main file")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except SplitError as exc:
        sys.stderr.write("split_kspstatepatcher: FAILED: %s\n" % exc)
        sys.exit(1)
