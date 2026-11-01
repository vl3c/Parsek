#!/usr/bin/env python3
"""Split LedgerOrchestrator.cs into partial-class files by responsibility.

A pure file move: every member of `static partial class LedgerOrchestrator` is
copied verbatim (its leading comments and attributes included) into the file its
area names, in its original relative order. Nothing inside a member is edited.

Rules the script enforces:
  * Every static field, constant, auto-property, event and the static constructor
    stays in the main file (LedgerOrchestrator.cs). C# leaves the execution order
    of static field initializers ACROSS partial-class files unspecified, so moving
    an initialized field could change initialization order. Only methods, computed
    properties (bodies, no backing field) and nested types move.
  * Every member must be named by exactly one area rule, or stay in the main file
    by being listed under MAIN (an unknown new member fails the run loudly, so a
    rerun on a newer main never silently drops or misfiles code).
  * The multiset of non-blank lines across the outputs equals the original's plus
    only the per-file headers (usings, namespace, class declaration and the closing
    braces). Any other difference fails the run.

How to re-run (from the repo root), e.g. after this branch conflicts with main:
    rm -f Source/Parsek/GameActions/LedgerOrchestrator.*.cs
    git checkout origin/main -- Source/Parsek/GameActions/LedgerOrchestrator.cs
    python3 scripts/refactor/split_ledgerorchestrator.py
Members new on main make the run fail with their names; add each to AREAS (or
MAIN) below and run again. `--outline` prints every member with its line span,
kind and current destination without writing anything. `--check` writes nothing
and only verifies that the mapping covers the file.
"""

import argparse
import collections
import os
import re
import sys

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
SRC_DIR = os.path.join(REPO, "Source", "Parsek", "GameActions")
MAIN_FILE = os.path.join(SRC_DIR, "LedgerOrchestrator.cs")

# Area -> (one-line description, member names). A name covers every overload of
# that name. Members keep their original relative order inside each output file.
AREAS = collections.OrderedDict([
    ("Commit", (
        "Recording and tree commit: filing a commit's rows, the walk after it, and "
        "the pending-science routing of a committed tree.",
        """
        RecordingScienceWindow OnRecordingCommitted FiledRecordingCommit
        FileRecordingCommitRows ResolveCommitReputationSeedOrigin RecordingCommitRowBuild
        BuildRecordingCommitRows TreeCommitRowPreview BuildTreeCommitRowsPreview
        PeekReputationSeedOriginForPreview ApplyRecordingCommitBuildClaims
        CompleteRecordingCommitAfterWalk
        NotifyLedgerTreeCommitted BuildTreePendingScienceRouting BuildRecordingScienceWindows
        ResolveTreePendingScienceOwnerRecordingId PickScienceWindowRecordingId
        RemovePendingSubjects BuildPendingScienceSubsetForRecording
        ResolveStandaloneCommitWindowStartUt RemovePendingSubjectsFromStatic
        FinalizeScopedPendingScienceCommit DoesUntaggedScienceCaptureMatchWindow
        BuildChainEndUtMap AdjustStartUtForChainGap AdjustStartUtForReFlyProvisional
        ResolveRewindPointUt ResolveRetiredReFlyProvisionalForCommit
        FileRetiredReFlyProvisionalRows
        """)),
    ("CommitRows", (
        "Rows a commit derives from a recording: capture-vs-ledger dedup, vessel build and "
        "recovery cost rows, kerbal assignment and death-penalty rows, crew extraction.",
        """
        DeduplicateAgainstLedger DeduplicateAgainst LegacyEconomicKeyMatches
        IsEventCaptureIdentified IsScienceCaptureIdentified HasScienceCaptureIdentityCandidate
        GetDedupOccurrenceUt GetActionKey
        CreateVesselCostActions BuildVesselCostActions AddVesselBuildCostActions
        AddVesselRecoveryCostActions CreateKerbalAssignmentActions BuildKerbalAssignmentActions
        CreateKerbalDeathRepPenaltyActions BuildKerbalDeathRepPenaltyActions CrewInfo
        ExtractCrewFromRecording NeedsCrewEndStatePopulation FindRecordingById
        """)),
    ("EarningsReconcile", (
        "Earnings reconciliation diagnostics: the commit window, KSC actions and the "
        "post-walk check of transformed rewards.",
        """
        SetResourceTrackingAvailabilityForTesting
        ReconcileEarningsWindow EarningsWindowStoreDeltas EarningsWindowEmittedDeltas
        ComputeEarningsWindowStoreDeltas LogEarningsWindowScopeSkipped
        IsKerbalDeathRepPenaltyRow HasKerbalDeathRepPenaltyRowForRecording
        IsMagnitudePairedKerbalDeathRepPenaltyRow IsKerbalDeathPairedVesselLossEvent
        LogEarningsWindowKerbalDeathPairing ComputeEarningsWindowEmittedDeltas
        AppendInWindowStrategyConversionRows IsSameRowIn EstimateInsideSeedRowRep
        ResolveInsideSeedRepNominal ResolveStoreRepPoolAtUT LogEarningsWindowBlindSpotFolds
        LogEarningsWindowActionSummary WarnOnEarningsWindowMismatches
        SumCommitWindowScienceDelta DoesScienceEventMatchAnyCommitAction LogReconcileWarnOnce
        LogScienceCommitReconcileDumpOnce
        ClassifyAction ReconcileKscAction ResourceChannelTag ClassifyPostWalk ReconcilePostWalk
        TryGetPersistedScienceActionWindow GetCollapsedScienceWindowStart
        GetScienceReconcileCollapsedHalfWidth EventMatchesRecordingScope
        DoesScienceEventMatchActionScope GetScienceChangedReasonKey
        FormatScienceEventForReconcileDump GetResourceTrackingAvailability
        LogReconcileSkippedOnce TryRegisterPostWalkDumpKey FormatFixed1 FormatFixed3
        """)),
    ("Recalculate", (
        "Recalculate-and-patch: the walk triggers, the clock-crossing bookkeeping, the core "
        "walk and applying its result to KSP.",
        """
        RecalculateAndPatch RecalculateAndPatchForCurrentTimelineUT
        RecalculateAndPatchForPostRewindFlightLoad RecalculateAndPatchForTimeJump
        RecalculateAndPatchForCurrentTimelineIfFutureActions ShouldCommitWalkUseLiveClock
        RecalculateAndPatchAfterCommit RecalculateIfKerbalReservationReleaseDue
        RecalculateAndPatchForLiveTimelineEvent RecalculateAndPatchForClockAdvance
        ExpireHeldThroughFloorAtTrustedClock RunAsClockAdvance InvalidateCommittedFutureSnapshot
        CaptureCommittedFutureSnapshot SplitCrossedCommittedFutureRows ComputeClockCrossing
        RecalculateAndPatchAfterTombstones
        RecalculateAndPatchCore ApplyHeldThroughFloorToCutoff BuildRecalculationActions
        LogRecalculationInputSummary ApplyRecalculatedStateToKsp ApplyReachedCommittedDismissals
        HasDismissalRow IsCommittedRowRecording PlanReachedCommittedDismissals
        ResolveCommittedDismissalsToMake CollectHireRowsNotAppliedByFunds
        ResolveCommittedHiresToMake BuildLedgerStructuralSnapshot
        BuildTombstonedFacilityIdsForPatch BuildTombstonedContractGuidsForPatch
        FoldContractReinstateCutoff BuildContractReinstateCutoffsForPatch
        BuildTombstonedScienceSpendingNodeIds BuildTombstonedActionIds HasActionsAfterUT
        IsCurrentUtReadyForCutoff TryGetNextActionUTAfter GetKspPatchDeferralReason
        RebuildCommittedScienceFromSurvivingLedger CommittedScienceCreditState
        BuildCommittedScienceSubjectCredits
        """)),
    ("Seeds", (
        "Initial resource baselines: the funds / science / reputation / progress seeds, "
        "the facility cost estimates and the budget-touch predicates.",
        """
        SeedInitialResourceBalances ProgressSeedProbe ReadProgressSeedProbe
        ReadProgressSeedProbeLive EnsurePreLedgerProgressSeed EnsureReputationSeedForCommit
        CurrencyPoolProbe ReadCurrencyPoolProbe FundsSeedDecision DecideInitialFundsSeed
        EnsureInitialFundsSeed TryGetFundsSeed EnsureLegacyFacilityUpgradeCostEstimates
        ReadFacilityCostTableProbe ReadFacilityCostTableProbeLive ReadFacilityLevelCosts
        EnsureInitialScienceSeed EnsureInitialReputationSeed
        TryReadUncommittedRecordingsAtSeedCapture ReadLoadedFlightStateUT ResolveSeedCaptureUT
        ReadUncommittedRecordingIdsLive AddTreeRecordingIds StampRowsRecordedBeforeSeedCapture
        StampLiveWriteRowAgainstSeed StampReputationRowsAgainstSeed CountUnenrichedMilestoneRows
        HasUnenrichedMilestoneRow RestampInsideSeedRowsAgainstCareerStartSeed
        ExpectedCurrencySingletonsForBaseline LogSeedRefusal LogBaselineCurrencyNotCaptured
        TryGetInitialResourceBaseline IsInitialResourceBaseline LedgerHasSeed
        LedgerHasFundsTimelineActions ActionTouchesFundsBudget LedgerHasScienceTimelineActions
        LedgerHasReputationTimelineActions ActionTouchesScienceBudget
        ActionTouchesReputationBudget IsAuthoritativeReduction
        """)),
    ("Persistence", (
        "Save and load: OnKspLoad, OnSave / OnLoad, old-save migration and the load-time "
        "ledger repairs.",
        """
        ResetMigrateOldSaveEventsForLoad ResetMigrateOldSaveEventsForTesting
        SetMigrateOldSaveEventsRanThisLoadForTesting GetMigrateOldSaveEventsRanThisLoadForTesting
        OnKspLoad KeepRowsOfRecordingsThatFailedToLoad ExcludeInertRecordingIds
        MigrateOldSaveEvents MigrateKerbalAssignments InheritKerbalAssignmentActionIds
        IsResourceImpactingAction KerbalAssignmentRepairRename KerbalAssignmentEndStateRewrite
        CarryTouristRoles KerbalAssignmentRepairStats ClassifyKerbalAssignmentRepair
        FindRepairRowMatch KerbalAssignmentActionsMatch PopulateUnpopulatedCrewEndStates
        OnSave OnLoad SeedKscSequenceCounterFromLoadedLedger KscSequenceCounterForTesting
        RepairLegacyPartPurchaseActionsOnLoad RepairZeroCostFacilityUpgradeActionsOnLoad
        TryRecoverBrokenLedgerOnLoad IsRecoverableEventType LedgerHasMatchingAction
        LedgerHasMatchingScienceEarning GetLedgerScienceEarningTotal MapEventTypeToActionType
        """)),
    ("LiveEvents", (
        "Live gameplay events written straight to the ledger: KSC spending, strategy "
        "conversions, live-only income, KSC science, rollout, and keeping irreversible "
        "live events on a discard.",
        """
        OnKscSpending OnKscSpendingBatch TryAddKscSpendingAction ScheduleKscSpendingRecalc
        ResolveDeferredLiveEventUT TryReadNowUTForDeferredRecalc ResolveKscDeferHost
        ShouldDeferKscRecalcOneFrame ShouldDeferKscReconcileOneFrame
        OnStrategyCurrencyConversion OnLiveOnlyIncome BuildStrategyConversionAction
        IsStrategyConversionRow IsIrreversibleLiveGameplayEvent IsFreeTechUnlockEvent
        PreserveIrreversibleLiveGameplayOnDiscard AppendRehomedDirectActions
        PreserveIrreversibleRestoreAttemptTailsOnDiscard ClearStrategyConversionTagsForDiscard
        IsRevertedFlightStrategyConversionRow PruneRevertedFlightStrategyConversionRows
        RemovePendingScienceSubjectsForRecordings TryRecordKscScienceSubject
        ResolveKscScienceRecordingId OnVesselRolloutSpending TryAdoptRolloutAction
        CanRecordingAdoptRolloutAction
        """)),
    ("Recovery", (
        "Vessel and crew recovery: recovery experience, recovery funds pairing and picking "
        "the recording a recovery belongs to.",
        """
        TryRecordRecoveryKerbalExperience OnRealVesselCrewRecovered OnRealVesselRecovered
        BuildRecoveryEventDedupKey TryFindRecoveryFundsEvent PendingRecoveryFundsCountForTesting
        OnRecoveryFundsEventRecorded FindBestPairingIndex FlushStalePendingRecoveryFunds
        RepairMissingRecoveryDedupKeys OnVesselRecoveryFunds TryAddVesselRecoveryFundsAction
        PickRecoveryRecordingId ResolveActiveReFlyProvisionalRecordingId
        IsConclusiveLaunchGuidMismatch FilterRecoveryCandidatesByLaunchGuid
        FindSessionProvisionalAmong IsPositiveLaunchGuidMatch IsGenuineSpawnPidMatch
        ResolveRecoveryAdmissibleRecordingIds PickRecoveryRecording
        """)),
    ("Affordability", (
        "Pending science debits and credits not yet in the ledger, the strategy science "
        "baseline, and the affordability checks stock UI blocks read.",
        """
        ComputePendingRecentKscTechResearchScienceDebit GetPendingRecentKscTechResearchScienceDebit
        ComputePendingUncommittedStrategyScienceDebit SumObservedStrategyInputScienceDebits
        SumCommittedExchangerStrategyScienceDebits ComputeUnmatchableStrategyScienceDebitBaseline
        ResolveEffectiveUnmatchableStrategyScienceBaseline CanStrategyScienceDebitRowStillLand
        CaptureUnmatchableStrategyScienceDebitBaselineOnLoad
        SetUnmatchableStrategyScienceDebitBaselineForTesting
        GetUnmatchableStrategyScienceDebitBaselineForTesting
        GetPendingUncommittedStrategyScienceDebit ComputePendingRecentKscScienceCredit
        GetPendingRecentKscScienceCredit CollectLedgerActionIds ComputeEffectiveAffordable
        CanAffordScienceSpending CanAffordFundsSpending
        """)),
])

# Methods / computed properties that deliberately stay in the main file: lifecycle,
# the clock reads, the module accessors and the facility slot helpers.
MAIN = set("""
    AllocateKscSequence Initialize TryEnsureKerbalsModuleForTombstoneRosterCleanup
    GetNowUT GetRawClockUT ResetForSaveLoad ResetForTesting UpdateSlotLimitsFromFacilities
    GetUpgradeableFacilityLevel GetContractSlots GetStrategySlots Science Funds Reputation
    Milestones Facilities Contracts Strategies Kerbals SetKerbalsForTesting IsInitialized
    HasFacilityActionsInRange
""".split())


# ---------------------------------------------------------------------------
# Lexer: per character, decide whether it is code (not in a comment / string /
# char literal), so brace and semicolon counting sees code only.
# ---------------------------------------------------------------------------

def code_mask(text):
    """Return a list of booleans, True where text[i] is code."""
    n = len(text)
    mask = [True] * n
    i = 0
    # Stack of interpolation contexts: each entry is the brace depth inside an
    # interpolation hole of an interpolated string, plus the string kind.
    interp_stack = []
    state = "code"
    verbatim = False
    interpolated = False
    while i < n:
        c = text[i]
        nxt = text[i + 1] if i + 1 < n else ""
        if state == "code":
            if c == "/" and nxt == "/":
                j = text.find("\n", i)
                j = n if j < 0 else j
                for k in range(i, j):
                    mask[k] = False
                i = j
                continue
            if c == "/" and nxt == "*":
                j = text.find("*/", i + 2)
                j = n if j < 0 else j + 2
                for k in range(i, j):
                    mask[k] = False
                i = j
                continue
            if c == "'":
                # char literal
                j = i + 1
                while j < n:
                    if text[j] == "\\":
                        j += 2
                        continue
                    if text[j] == "'":
                        break
                    j += 1
                for k in range(i, j + 1):
                    mask[k] = False
                i = j + 1
                continue
            if c in "$@" and nxt in '"$@':
                # prefixes: $" @" $@" @$"
                j = i
                verbatim = False
                interpolated = False
                while j < n and text[j] in "$@":
                    if text[j] == "$":
                        interpolated = True
                    else:
                        verbatim = True
                    j += 1
                if j < n and text[j] == '"':
                    for k in range(i, j + 1):
                        mask[k] = False
                    i = j + 1
                    state = "string"
                    continue
            if c == '"':
                verbatim = False
                interpolated = False
                mask[i] = False
                i += 1
                state = "string"
                continue
            if interp_stack:
                if c == "{":
                    interp_stack[-1][0] += 1
                elif c == "}":
                    if interp_stack[-1][0] == 0:
                        # end of the hole: back into the string
                        _, verbatim, interpolated = interp_stack.pop()
                        mask[i] = False
                        i += 1
                        state = "string"
                        continue
                    interp_stack[-1][0] -= 1
            i += 1
            continue
        # state == "string"
        mask[i] = False
        if verbatim:
            if c == '"' and nxt == '"':
                mask[i + 1] = False
                i += 2
                continue
            if c == '"':
                state = "code"
                i += 1
                continue
        else:
            if c == "\\":
                if i + 1 < n:
                    mask[i + 1] = False
                i += 2
                continue
            if c == '"':
                state = "code"
                i += 1
                continue
        if interpolated:
            if c == "{" and nxt == "{":
                mask[i + 1] = False
                i += 2
                continue
            if c == "}" and nxt == "}":
                mask[i + 1] = False
                i += 2
                continue
            if c == "{":
                interp_stack.append([0, verbatim, interpolated])
                state = "code"
                i += 1
                continue
        i += 1
    if state != "code" or interp_stack:
        raise SystemExit("lexer: unterminated string or interpolation at end of file")
    return mask


Member = collections.namedtuple("Member", "start end kind name header")
# start/end: 0-based inclusive line indices of the chunk (leading trivia included).


def parse(text):
    """Return (prefix_lines, members, suffix_lines, lines).

    prefix: everything up to and including the class-declaration line with its '{'.
    suffix: the class's closing '}' line and everything after it.
    """
    lines = text.split("\n")
    if lines and lines[-1] == "":
        lines = lines[:-1]
        trailing_newline = True
    else:
        trailing_newline = False
    if not trailing_newline:
        raise SystemExit("input does not end with a newline")
    mask = code_mask(text)
    # line start offsets
    offs = []
    o = 0
    for ln in lines:
        offs.append(o)
        o += len(ln) + 1

    def line_of(pos):
        lo, hi = 0, len(offs) - 1
        while lo < hi:
            mid = (lo + hi + 1) // 2
            if offs[mid] <= pos:
                lo = mid
            else:
                hi = mid - 1
        return lo

    class_re = re.compile(r"^\s*internal static partial class LedgerOrchestrator\s*$")
    class_line = None
    for idx, ln in enumerate(lines):
        if class_re.match(ln):
            if class_line is not None:
                raise SystemExit("two class declarations found")
            class_line = idx
    if class_line is None:
        raise SystemExit("class declaration 'internal static partial class LedgerOrchestrator' not found")
    if lines[class_line + 1].strip() != "{":
        raise SystemExit("expected '{' on the line after the class declaration")
    body_open_line = class_line + 1
    depth = 2  # inside namespace + class
    pos = offs[body_open_line] + len(lines[body_open_line]) + 1
    n = len(text)

    members = []
    chunk_start_line = body_open_line + 1
    seen_code = False
    deep = False  # went above depth 2 within the current member
    header_chars = []
    class_close_line = None

    def next_code_char(p):
        while p < n and (not mask[p] or text[p].isspace()):
            p += 1
        return text[p] if p < n else ""

    while pos < n:
        c = text[pos]
        if not mask[pos]:
            pos += 1
            continue
        had_code = seen_code
        if not c.isspace():
            seen_code = True
        if depth == 2 and c not in "{};" and not deep:
            header_chars.append(c)
        if c == "{":
            if depth == 2:
                deep = True
            depth += 1
        elif c == "}":
            depth -= 1
            if depth == 1:
                # class body closed
                if had_code:
                    raise SystemExit("unterminated member before the class's closing brace")
                class_close_line = line_of(pos)
                break
            if depth == 2:
                nc = next_code_char(pos + 1)
                if nc not in (";", ",", ")", ".", "=", "("):
                    end_line = line_of(pos)
                    members.append(_close(lines, mask, offs, chunk_start_line, end_line, pos,
                                          "".join(header_chars)))
                    chunk_start_line = end_line + 1
                    seen_code = False
                    deep = False
                    header_chars = []
        elif c == ";" and depth == 2:
            end_line = line_of(pos)
            members.append(_close(lines, mask, offs, chunk_start_line, end_line, pos,
                                  "".join(header_chars)))
            chunk_start_line = end_line + 1
            seen_code = False
            deep = False
            header_chars = []
        pos += 1
    if class_close_line is None:
        raise SystemExit("class closing brace not found")
    # Anything between the last member and the class close must be blank/comment.
    for k in range(chunk_start_line, class_close_line):
        if lines[k].strip():
            raise SystemExit("unexpected trailing content inside the class at line %d" % (k + 1))
    if lines[class_close_line].strip() != "}":
        raise SystemExit("class closing brace line is not a bare '}'")
    prefix = lines[:body_open_line + 1]
    trailing_blank = lines[chunk_start_line:class_close_line]
    suffix = lines[class_close_line:]
    return prefix, members, trailing_blank, suffix, lines


def _close(lines, mask, offs, start, end_line, pos, header):
    # The terminator must be the last code on its line.
    line_end = offs[end_line] + len(lines[end_line])
    for p in range(pos + 1, line_end):
        if mask[p] and not lines[end_line][p - offs[end_line]].isspace():
            raise SystemExit("member ends mid-line at line %d" % (end_line + 1))
    kind, name = classify(header)
    return Member(start, end_line, kind, name, header)


_MODIFIERS = {"public", "private", "internal", "protected", "static", "readonly", "const",
              "unsafe", "extern", "new", "override", "virtual", "sealed", "abstract",
              "partial", "volatile", "async"}


def classify(header):
    h = re.sub(r"\[[^\[\]]*(\[[^\[\]]*\][^\[\]]*)*\]", " ", header)  # strip attributes
    h = " ".join(h.split())
    m = re.search(r"\b(class|struct|enum|interface)\s+(\w+)", h)
    if m and "(" not in h.split(m.group(0))[0]:
        return "type", m.group(2)
    m = re.search(r"\bdelegate\b.*?\b(\w+)\s*(<[^>]*>)?\s*\(", h)
    if m:
        return "delegate", m.group(1)
    if re.search(r"\bevent\b", h):
        m = re.search(r"(\w+)\s*(=|$)", h)
        return "event", m.group(1) if m else "?"
    if re.search(r"\bconst\b", h):
        m = re.search(r"(\w+)\s*=", h)
        return "const", m.group(1)
    # Strip a field initializer before looking for a parameter list.
    eq = _top_level_eq(h)
    before = h if eq < 0 else h[:eq]
    if "(" in before:
        name = re.search(r"(\w+)\s*(<[^()]*>)?\s*$", before.split("(")[0]).group(1)
        if name == "LedgerOrchestrator":
            return "cctor", name
        return "method", name
    toks = before.split()
    name = toks[-1] if toks else "?"
    if eq < 0 and "=>" in h:
        return "property", h.split("=>")[0].split()[-1]
    if re.search(r"\b(get|set)\s*;", header):
        return "autoprop", name
    if eq < 0 and re.search(r"\bget\b", header) is not None and "{" in header:
        return "property", name
    return "field", name


def _top_level_eq(h):
    depth = 0
    for i, ch in enumerate(h):
        if ch in "(<[":
            depth += 1
        elif ch in ")>]":
            depth -= 1
        elif ch == "=" and depth == 0:
            nxt = h[i + 1] if i + 1 < len(h) else ""
            prv = h[i - 1] if i > 0 else ""
            if nxt in "=>" or prv in "=!<>":
                continue
            return i
    return -1


MOVABLE_KINDS = {"method", "property", "type", "delegate"}


KEEP_KINDS = {"const", "field", "autoprop", "event", "cctor"}


def build_destinations(members):
    """Map each member index to an area name, or None for the main file."""
    name_to_area = {}
    problems = []
    for area, (_, names) in AREAS.items():
        for nm in names.split():
            if nm in name_to_area:
                problems.append("member %s listed in two areas (%s, %s)" % (nm, name_to_area[nm], area))
            name_to_area[nm] = area
    for nm in MAIN:
        if nm in name_to_area:
            problems.append("member %s listed both in MAIN and area %s" % (nm, name_to_area[nm]))
    present = set()
    dest = []
    for m in members:
        present.add(m.name)
        if m.kind in KEEP_KINDS:
            if m.name in name_to_area:
                problems.append("%s %s (line %d) holds state and must stay in the main file"
                                % (m.kind, m.name, m.start + 1))
            dest.append(None)
            continue
        if m.kind not in MOVABLE_KINDS:
            problems.append("unknown member kind %s for %s" % (m.kind, m.name))
            dest.append(None)
            continue
        if m.name in name_to_area:
            dest.append(name_to_area[m.name])
        elif m.name in MAIN:
            dest.append(None)
        else:
            problems.append("%s %s (lines %d-%d) is not mapped: add it to an area or MAIN"
                            % (m.kind, m.name, m.start + 1, m.end + 1))
            dest.append(None)
    for nm in sorted(set(name_to_area) | MAIN):
        if nm not in present:
            problems.append("mapped member %s no longer exists in the source" % nm)
    if problems:
        raise SystemExit("mapping problems:\n  " + "\n  ".join(problems))
    return dest


def chunk_lines(lines, m):
    return lines[m.start:m.end + 1]


def strip_leading_blank(chunk):
    i = 0
    while i < len(chunk) and not chunk[i].strip():
        i += 1
    return chunk[i:]


def render(prefix, members, trailing_blank, suffix, lines, dest):
    usings_end = None
    for idx, ln in enumerate(prefix):
        if ln.startswith("namespace "):
            usings_end = idx
            break
    if usings_end is None:
        raise SystemExit("namespace line not found in the file prefix")
    usings = prefix[:usings_end]          # usings + the blank line after them
    namespace_line = prefix[usings_end]
    class_decl = prefix[-2]
    class_open = prefix[-1]
    header = usings + [namespace_line, "{", class_decl, class_open]
    footer = suffix                        # "    }" and "}"

    outputs = collections.OrderedDict()
    main_body = []
    for m, d in zip(members, dest):
        if d is None:
            main_body.extend(chunk_lines(lines, m))
    outputs[MAIN_FILE] = prefix + main_body + trailing_blank + suffix
    added = []
    for area in AREAS:
        body = []
        for m, d in zip(members, dest):
            if d == area:
                chunk = chunk_lines(lines, m)
                body.extend(strip_leading_blank(chunk) if not body else chunk)
        if not body:
            raise SystemExit("area %s received no members" % area)
        path = os.path.join(SRC_DIR, "LedgerOrchestrator.%s.cs" % area)
        outputs[path] = header + body + footer
        added.extend(header + footer)
    return outputs, added


def verify(original_lines, outputs, added):
    orig = collections.Counter(l for l in original_lines if l.strip())
    out = collections.Counter()
    for content in outputs.values():
        out.update(l for l in content if l.strip())
    expected = orig + collections.Counter(l for l in added if l.strip())
    if out != expected:
        extra = out - expected
        missing = expected - out
        msg = ["non-blank line multiset mismatch"]
        for l, c in list(extra.items())[:20]:
            msg.append("  extra x%d: %r" % (c, l))
        for l, c in list(missing.items())[:20]:
            msg.append("  missing x%d: %r" % (c, l))
        raise SystemExit("\n".join(msg))
    total_nonblank = sum(orig.values())
    return total_nonblank


def main_outline(members, dest):
    for m, d in zip(members, dest):
        print("%5d %5d %-8s %-50s -> %s" % (m.start + 1, m.end + 1, m.kind, m.name, d or "(main)"))


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--outline", action="store_true", help="print the member table and exit")
    ap.add_argument("--check", action="store_true", help="verify the mapping, write nothing")
    ap.add_argument("--input", default=MAIN_FILE)
    args = ap.parse_args()
    with open(args.input, "r", encoding="utf-8", newline="") as f:
        text = f.read()
    if "\r" in text:
        raise SystemExit("input has CR characters; expected LF line endings")
    for area in AREAS:
        path = os.path.join(SRC_DIR, "LedgerOrchestrator.%s.cs" % area)
        if os.path.exists(path) and not (args.outline or args.check):
            raise SystemExit("%s already exists: restore the unsplit file and remove the partial "
                             "files first (see the module docstring)" % path)
    prefix, members, trailing_blank, suffix, lines = parse(text)
    if args.outline:
        try:
            dest = build_destinations(members)
        except SystemExit as e:
            print(e, file=sys.stderr)
            dest = [None] * len(members)
        main_outline(members, dest)
        return 0
    dest = build_destinations(members)
    outputs, added = render(prefix, members, trailing_blank, suffix, lines, dest)
    total = verify(lines, outputs, added)
    for path, content in outputs.items():
        print("%-70s %6d lines" % (os.path.relpath(path, REPO), len(content)))
    print("verified: %d non-blank source lines accounted for across %d files"
          % (total, len(outputs)))
    if args.check:
        return 0
    for path, content in outputs.items():
        with open(path, "w", encoding="utf-8", newline="") as f:
            f.write("\n".join(content) + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
