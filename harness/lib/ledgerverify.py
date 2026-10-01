"""The ledger-oracle verifier's decision core (M-B2), shared by ``run.py`` and the
ledger mutation checker (``mutledger``, MUTATION-CHECK-PHASE-2 PR 2).

``run.py`` used to hold this composition inline in ``_run_ledger_oracle``: the
leg-A manifest build (stock-award capture + seam parse), the seam-rejection reds,
``oracle.compute_expected`` + ``diff_expected_vs_parsed``, the stock-award
cross-check, the world vessel and roster diffs, and the tooling routes. It moved
here unchanged so the mutation checker replays the SAME code a flight runs rather
than a copy that could drift. ``run.py`` keeps the I/O: it passes a ``log``
callback (its logger, tag ``Verify``) and an ``on_manifest`` callback (the
``<runId>.manifest.json`` write), both called at the points the inline code
logged and wrote, so the run log is byte-identical.

Pure apart from the two callbacks: no file I/O, no clock. ASCII only; stdlib only.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Callable, Dict, List, Optional, Tuple

import hlib
import oracle

# The captured-award facet -> careerSave/diff pool name (oracle diff facet names).
AWARD_FACET_TO_DIFF = {"funds": "funds", "science": "sciencePool", "reputation": "reputation"}

LogFn = Callable[[str, str], None]


def _no_log(_level: str, _msg: str) -> None:
    return None


@dataclass
class LedgerVerdict:
    """The verifier row plus what produced it. ``tooling`` is the INVALID(tooling)
    route (an active ledger check must never green on a missing input);
    ``divergences`` is every hard and report-only divergence; ``manifest`` is the
    accumulated leg-A manifest (None when no ledger block is declared or the run
    stopped at a tooling route before the manifest was built)."""
    result: Dict
    drift: bool
    tooling: bool
    divergences: List = field(default_factory=list)
    manifest: Optional[Dict] = None


def manifest_entry_to_dict(e) -> Dict:
    """Serialize an oracle.ManifestEntry to a stable-keyed dict for the accumulated
    manifest artifact."""
    return {"ut": e.ut, "seq": e.seq, "kind": e.kind, "funds": e.funds,
            "science": e.science, "reputation": e.reputation, "repMode": e.rep_mode,
            "subjectIds": list(e.subject_ids), "contractGuid": e.contract_guid,
            "provenance": e.provenance, "rec3Row": e.rec3_row,
            "utWindow": (list(e.ut_window) if e.ut_window is not None else None),
            "stockReason": list(e.stock_reasons)}


def world_declared_vessels(world_block: Dict) -> List[Dict]:
    """The declared vessel entries under ``[[expectations.world.vessels.entry]]``
    (design ~502)."""
    vessels = (world_block or {}).get("vessels", {}) or {}
    return vessels.get("entry", []) or []


def build_manifest(ledger_block: Dict, log_text: str, seed, run_id: str,
                   log: LogFn = _no_log) -> Tuple:
    """Build leg A (design ~366): the seam-declared author-constant entries (the set
    the oracle sums into EXPECTED) + the stock-log-captured awards (cross-checked as
    corroborating / unexpected). Returns ``(seam_entries, deduped_captured,
    seam_reject_errors, manifest)``; ``manifest`` is the accumulated
    ``<runId>.manifest.json`` body (``entries`` = the oracle-consumed seam entries,
    ``capturedRaw`` = every matched stock line). ``seam_reject_errors`` is the tuple
    of per-entry rejection reasons the caller reds each as a dropped-expected-effect
    PARSEK-FAIL (edge 18).

    v1 reconciliation (design ambiguity resolved): the accumulated ``entries`` the
    oracle CONSUMES for EXPECTED are the seam-declared author constants ONLY. The
    Mental Model invariant (~199) is binding -- an empty-manifest B10 must compute
    ``expected == seed`` so the save-diff catches an award the capture MISSED -- so
    captured awards are NOT summed into expected; they are cross-checked
    (corroborate a seam entry, or red as an unexpected award, edge 4). ``capturedRaw``
    records every captured line for audit."""
    raw_seam = ledger_block.get("manifest", []) or []

    # Capture FIRST: the deduped stock-log awards are the fill-from-capture pool a
    # funds-facet seam entry (null funds amount) draws from (design edge 18 fill path).
    # They are parsed into ManifestEntry objects so the seam parse can match on
    # (seqKey, kind, contractGuid, funds-facet). Captured awards are always well-formed
    # deltas (hlib guarantees it), so a captured-entry parse error is a capture-tooling
    # anomaly, warn-logged (never a scenario RED, unlike a seam-declared rejection).
    cap = hlib.parse_stock_award_lines(log_text)
    deduped = hlib.dedupe_captured_awards(cap.captured)
    captured_parse = oracle.parse_manifest_entries([c.to_entry_dict() for c in deduped])
    for err in captured_parse.errors:
        log("warn", "manifest captured entry rejected (capture tooling): %s" % err)
    captured_entries = captured_parse.entries

    seam_parse = oracle.parse_manifest_entries(raw_seam, captured=captured_entries)
    for err in seam_parse.errors:
        log("warn", "manifest seam entry rejected: %s" % err)
    seam_entries = seam_parse.entries

    log("info", "manifest-capture stockLines=%d deduped=%d seamDeclared=%d seamRejected=%d accumulated=%d"
        % (cap.stock_lines, len(deduped), len(seam_entries), len(seam_parse.errors),
           len(seam_entries) + len(deduped)))

    manifest = {
        "schema": oracle.SCHEMA_VERSION,
        "runId": run_id,
        # Seed audit copy in the SINGLE careerSave-block shape (key `sciencePool`, NOT
        # `science`) so it round-trips through oracle.parse_seed_baseline identically to
        # the analyzer block it was captured from (review BLOCKER 1 / SF3). The ledger
        # mutation checker reads this copy back as the archived seed.
        "seed": {"funds": seed.funds, "sciencePool": seed.science, "reputation": seed.reputation,
                 "hasFunds": seed.has_funds, "hasScience": seed.has_science, "hasRep": seed.has_rep},
        "entries": [manifest_entry_to_dict(e) for e in seam_entries],
        "capturedRaw": [dict(c.to_entry_dict(), rawLine=c.raw_line) for c in cap.captured],
    }
    return seam_entries, deduped, tuple(seam_parse.errors), manifest


def _invalid(reason: str) -> Dict:
    return {"status": oracle.ORACLE_STATUS_INVALID, "subkind": "tooling",
            "reason": reason, "hardDivergences": 0, "reportOnly": 0,
            "utWindow": [None, None]}


def evaluate(ledger_block: Optional[Dict], world_block: Optional[Dict],
             career_block: Optional[Dict], seed, log_text: str, run_id: str = "",
             log: LogFn = _no_log,
             on_manifest: Optional[Callable[[Dict], None]] = None) -> LedgerVerdict:
    """Run the ledger-oracle verifier (design ~444) over its inputs. ``seed`` is the
    ``oracle.SeedBaseline`` (or None); ``career_block`` the produced save's parsed
    ``careerSave`` block (or None when absent); ``log_text`` the produced KSP.log.

    Edge 13: an ABSENT careerSave block on an ACTIVE ledger verifier is
    INVALID(tooling) (an active ledger check must never green on a missing input).
    """
    if career_block is None:
        log("warn", "verify ledgerOracle status=INVALID subkind=tooling: careerSave block absent from analysis.json")
        return LedgerVerdict(_invalid("careerSave block absent from analysis.json"), False, True)
    # A produced-save careerSave that the analyzer could not parse (parsed=false) on an
    # ACTIVE ledger verifier is the same tooling condition as an ABSENT block (edge 13
    # symmetry / edge 15): the diff would net all-facets-absent into PARSEK-FAIL
    # missing-facet drift, which is the WRONG signal (it is an analyzer/config parse
    # fault, not a Parsek defect). Route to INVALID(tooling), never a false PARSEK-FAIL
    # (item 10). Facet-absence (Sandbox/Science) is signalled by hasX flags with
    # parsed=TRUE, not by parsed=false, so this only fires on a genuine parse failure.
    if career_block.get("parsed") is False:
        log("warn", "verify ledgerOracle status=INVALID subkind=tooling: produced careerSave parsed=false (analyzer could not parse the produced save)")
        return LedgerVerdict(_invalid(
            "produced careerSave parsed=false (analyzer could not parse the produced save)"),
            False, True)

    tol = oracle.default_tolerances()
    divergences: List = []
    manifest: Optional[Dict] = None

    if ledger_block is not None:
        if seed is None:
            # Defensive: an active ledger verifier with no seed should have been a
            # pre-launch terminal INVALID; fail closed rather than green.
            log("warn", "verify ledgerOracle status=INVALID subkind=tooling: no seed baseline for an active ledger verifier")
            return LedgerVerdict(_invalid("no seed baseline"), False, True)
        rec3 = bool(ledger_block.get("rec3CarveOut", False))
        rec3_whitelist = (ledger_block.get("rec3Whitelist", []) or []) if rec3 else []
        seam_entries, captured, seam_errors, manifest = build_manifest(
            ledger_block, log_text, seed, run_id, log)
        if on_manifest is not None:
            on_manifest(manifest)
        # Design edge 18: a rejected seam entry (unknown kind / balance amount /
        # state-dependent null / un-fillable funds) is a DROPPED expected effect that
        # would false-PASS if silently dropped; each rejection reds PARSEK-FAIL(ledger).
        for err in seam_errors:
            divergences.append(oracle.OracleDivergence(
                facet="ledger", kind="manifest-parse-error", identity="",
                expected=None, parsed=None, ut_window=(None, None), hard=True,
                detail="manifest entry rejected (a dropped expected effect can false-PASS): %s" % err))
            log("warn", "ledger manifest-parse-error (hard): %s" % err)
        expected = oracle.compute_expected(seed, seam_entries, tol, rec3_whitelist)
        log("info", "oracle-expected funds=%s science=%s rep=%s subjects=%d activeContracts=%d rec3CarveOut=%s"
            % (expected.funds, expected.science, expected.reputation,
               len(expected.subject_science), len(expected.active_contract_guids), rec3))
        for row in expected.rec3_residual_rows:
            log("info", "oracle: rec3 residual retained row=%s expecting [Rec-3 residual]" % row)
        divergences += oracle.diff_expected_vs_parsed(expected, career_block, tol, rec3_whitelist)
        # Zero-delta cross-check (design ~482 / edge 4): a captured award not
        # explained by a seam entry is an unexpected stock award.
        #
        # HARD ONLY WHEN THE SCENARIO ARMS IT (`captureCrossCheck = "gate"`, declared
        # by exactly ONE committed spec since 2026-07-31: `CL-2-pod-impact-ledger`,
        # armed over three flights against the real game - see known-gate 3; every
        # other spec is still report-only).
        #
        # Until the 2026-07-29 pattern rewrite this loop had an
        # always-empty input - STOCK_AWARD_PATTERNS matched shapes no KSP build emits -
        # so the hard drift it wrote was unreachable. Turning a working capture and a
        # live gate on in one step would red scenarios against an award baseline nobody
        # has measured, so the mode knob decides and the default reports.
        #
        # WHAT AN ARMING OPERATOR IS ACTUALLY UP AGAINST (corrected 2026-07-29): the
        # capture sees REPUTATION ONLY. KSP logs no funds and no science award line, so
        # the three milestone FUNDS awards a career pad hop trips are invisible here -
        # they move the produced save (where the seam-declared-vs-save diff catches
        # them) but can never surface as unexpected captured awards. The only
        # undeclared awards a career flight can produce on this path are the stock
        # `Progression` rep awards. That makes the baseline far smaller than the
        # original deferral assumed, and a scenario that declares its rep effects can
        # realistically arm the gate.
        cross_check_hard = hlib.capture_cross_check_gates(ledger_block)
        # The corroboration key is (seqKey, facet, amount-within-tolerance), NOT kind:
        # a captured kind is generic and a seam kind is a scenario semantic, so joining
        # on kind made every award - including the scenario's own declared one - read
        # unexpected. Pass the RUN's tolerances so the reputation window is the same
        # one the diff uses (a seam entry is NOMINAL, a stock rep line is post-curve).
        capture_tol = {"funds": tol.funds, "science": tol.science,
                       "reputation": tol.reputation}
        for c in hlib.unmatched_captured_awards(seam_entries, captured, capture_tol):
            facet = AWARD_FACET_TO_DIFF.get(c.facet, c.facet)
            # Edge 4 (~582): the UT window is the captured line's UT, or the ORDINAL
            # seq when the award had no UT-stamped [Parsek] neighbor (never [None, None],
            # which would strip the drift's only positional anchor). This is the RAW
            # NUMERIC anchor, not the type-tagged seq_key (the window bounds must stay
            # comparable for _aggregate_ut_window's min/max; the tag lives only in the
            # matcher keys).
            aw = c.ut if c.ut is not None else c.seq
            divergences.append(oracle.OracleDivergence(
                facet=facet, kind="unexpected-award",
                identity=(c.contract_guid or c.subject_id or c.reason or ""),
                expected=None, parsed=c.amount, ut_window=(aw, aw),
                hard=cross_check_hard,
                detail="unexpected stock award kind=%s facet=%s reason=%s amount=%r ut=%s "
                       "seqKey=%r crossCheck=%s line=%r"
                       % (c.kind, c.facet, c.reason or "(none)", c.amount, c.ut, c.seq_key,
                          "gate" if cross_check_hard else "report", c.raw_line)))
            log("warn", "manifest-capture: unexpected stock award ut=%s kind=%s "
                        "reason=%s hard=%s line='%s'"
                % (c.ut, c.kind, c.reason or "(none)", cross_check_hard, c.raw_line))

    if world_block is not None:
        declared = world_declared_vessels(world_block)
        parsed_vessels = career_block.get("vessels", []) if isinstance(career_block, dict) else []
        # report_phantoms stays FALSE (the default) DELIBERATELY (review N2): the
        # [expectations.world] block is a resource WHITELIST, not an exhaustive census,
        # so an undeclared parsed vessel (stray debris, other craft) is expected and
        # emitting a report-only phantom per save vessel would be pure noise. Phantoms
        # are report-only and can never red (design ~516), so suppressing them changes
        # no verdict; the classification remains available for a future census facet.
        world_divs = oracle.diff_world_vessels(declared, parsed_vessels, tol)
        for d in world_divs:
            log("info", "world-vessel corr=%s kind=%s expected=%s parsed=%s hard=%s detail=%s"
                % (d.identity, d.kind, d.expected, d.parsed, d.hard, d.detail))
        divergences += world_divs

        # Roster sub-facet: DEFERRED at M-B2 ("no CareerSaveSnapshot roster") and
        # un-deferred by the career-ledger lane once CareerSaveParser gained the
        # ROSTER parse and the analyzer exported `careerSave.roster`. Correlated by
        # name, present/absent claims only, and HARD - the claim IS the scenario's
        # action. A block declaring nothing returns nothing, so every spec that does
        # not declare a roster is byte-unaffected.
        roster_block = (world_block or {}).get("roster") or {}
        # A DECLARED roster claim against a produced save that carries no roster facet
        # (`hasRoster` missing/false with parsed=true) is a TOOLING condition, not a
        # Parsek defect: the analyzer never exported the surface the claim reads, so
        # there is nothing to diff. Same precedent as the parsed=false route above
        # (edge 13 / edge 15) - INVALID(tooling), never a false PARSEK-FAIL(ledger).
        # Fail-closed is preserved: the run still reds, it just reds in the bucket that
        # names the actual fault. `diff_world_roster` keeps its own defensive guard for
        # any caller that reaches it without this pre-check.
        roster_declared = bool((roster_block.get("present") or ())
                               or (roster_block.get("absent") or ()))
        if roster_declared and not bool(career_block.get("hasRoster", False)):
            reason = ("roster assertions declared but the produced careerSave carries no "
                      "roster facet (hasRoster=false; analyzer did not export it)")
            log("warn", "verify ledgerOracle status=INVALID subkind=tooling: %s" % reason)
            return LedgerVerdict(_invalid(reason), False, True, divergences, manifest)
        roster_divs = oracle.diff_world_roster(roster_block, career_block)
        for d in roster_divs:
            log("info", "world-roster name=%s kind=%s hard=%s detail=%s"
                % (d.identity or "(facet)", d.kind, d.hard, d.detail))
        divergences += roster_divs
        log("verbose", "world: roster sub-facet declared=%s present=%d absent=%d divergences=%d"
            % (bool(roster_block),
               len(roster_block.get("present") or ()),
               len(roster_block.get("absent") or ()),
               len(roster_divs)))

    for d in divergences:
        if d.hard:
            log("warn", "ledger-drift facet=%s id=%s expected=%s parsed=%s utWindow=[%s,%s]"
                % (d.facet, d.identity, d.expected, d.parsed, d.ut_window[0], d.ut_window[1]))
        else:
            log("info", "ledger-diff facet=%s id=%s expected=%s parsed=%s hard=False"
                % (d.facet, d.identity, d.expected, d.parsed))

    result = oracle.build_oracle_result(divergences)
    ledger_drift = oracle.has_hard_drift(divergences)
    # `crossCheck=` is UNCONDITIONAL on purpose. It used to be emitted only inside
    # the per-unmatched-award loop, so a run with zero unmatched awards left NO
    # archived trace of whether the check was armed - the armed CL-2 flight
    # `2026-07-31_1645` produced a verifier block byte-identical to the report-mode
    # run before it, and "armed and flown green" rested on the spec's state at the
    # time rather than on evidence. A positive, grep-stable token beats an absence
    # proof; the next arming session can cite the log instead of the narrative.
    cross_mode = ((ledger_block or {}).get(hlib.LEDGER_CAPTURE_CROSS_CHECK_KEY, "report")
                  if ledger_block is not None else "n/a")
    log("info", "verify ledgerOracle status=%s hardDivergences=%d reportOnly=%d crossCheck=%s"
        % (result["status"], result["hardDivergences"], result["reportOnly"], cross_mode))
    return LedgerVerdict(result, ledger_drift, False, divergences, manifest)
