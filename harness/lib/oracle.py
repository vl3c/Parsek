"""Pure ledger oracle for the M-B2 automated-testing module (leg A vs leg B).

This module is the L-track's independent oracle. It turns an ACCUMULATED action
manifest (leg A: event-time stock emissions + author constants + the fixture
seed) plus a seed baseline into EXPECTED career totals, then diffs those against
the produced save's parsed ``careerSave`` block (leg B: KSP's own serialization,
parsed by ``CareerSaveParser`` with zero ledger involvement). The oracle NEVER
reaches for a Parsek-computed number -- that independence is the whole point of
the L-track (design Problem / Mental Model; game-actions design 15.6 warns of the
circularity to avoid).

Everything here is side-effect-free: NO KSP, NO network, NO filesystem, NO wall
clock. The thin imperative shell (``run.py`` -- a SEPARATE module, not built here)
does all I/O: it greps the produced ``KSP.log`` for stock-award lines (the CAPTURE
side, ``hlib``), reads the ``.analysis.json`` ``careerSave`` block off disk, runs
the analyzer subprocess for the seed, writes ``<runId>.manifest.json``, and calls
into here for the parse / compute / diff decisions.

Covered here (design "Oracle expected-state + diff types" ~330 / "Oracle
computation" ~412 / "The ledger-oracle verifier" ~444):
  - the accumulated-manifest / manifest-entry parse + validation
    (``parse_seed_baseline`` / ``parse_manifest_entries``), rejecting malformed
    entries with precise reasons -- the unknown-kind reject, the REQUIRED
    author-constant rule for state-dependent facets (per-subject science,
    reputation; fill-from-capture FORBIDDEN there), and the every-captured-amount-
    is-a-DELTA rule (a post-grant BALANCE is inadmissible)
  - ``compute_expected(seed, entries, tolerances, rec3_whitelist)`` -- the
    per-facet expected totals (funds / science HARD linear pools, reputation via
    the SetReputation-semantics curve, per-subject science + contracts report-only,
    the Rec-3 ratified-residual carve-out)
  - ``apply_rep_curve`` -- the Python port of
    ``ReputationModule.ApplyReputationCurve`` (decompiled stock keyframes, Unity
    Hermite tangent evaluation; the plan-6 / game-actions-15.1 curve exception)
  - ``diff_expected_vs_parsed`` / ``diff_world_vessels`` -- the expected-vs-parsed
    diff mirroring the ``LedgerGroundTruthDiff`` facet policy (funds / science pool
    / reputation HARD; per-subject science / contracts / vessel-pid-only
    REPORT-ONLY), each divergence carrying its bounding manifest UT window
  - ``build_oracle_result`` / ``serialize_oracle_result`` -- the deterministic
    ``ledgerOracle`` verifier-row (``status`` / ``hardDivergences`` / ``reportOnly``
    / ``utWindow``) the harness-core verifier-chain slot 8 fills.

Design authority: docs/dev/design-autotest-ledger-oracle.md (Module M-B2).
Consumed contracts pinned against their PUBLIC surfaces: the ``CareerSaveSnapshot``
field set (funds / science pool + per-subject / reputation / active contract guids
/ per-vessel resource totals), ``FacetTolerances.Default`` (funds 1.0, science 0.1,
rep 0.1, subject 0.1), the ``ReputationModule.ApplyReputationCurve`` keyframes
(``GameActions/ReputationModule.cs``), never their internals.

ASCII only; stdlib only. Blends with ``hlib.py`` conventions (frozen dataclasses,
precise error strings, deterministic serialization).
"""

from __future__ import annotations

import json
import math
from dataclasses import dataclass, field
from typing import Dict, List, Optional, Sequence, Tuple

SCHEMA_VERSION = 1

# ---------------------------------------------------------------------------
# Vocabulary tables (design Data Model "Manifest entry" ~220 / Terminology ~79).
# ---------------------------------------------------------------------------

# The `kind` enum (design ~255, open + additive). v1 validates the enum but the
# B10 flagship declares no entries. The four contract kinds drive the report-only
# active-contract-guid transitions.
KINDS: Tuple[str, ...] = (
    "contract-accept", "contract-complete", "contract-fail",
    "science-transmit", "science-recover", "milestone",
    "facility-upgrade", "facility-refund", "tech-unlock",
    "kerbal-hire", "kerbal-dismiss", "strategy-activate", "strategy-convert",
    "vessel-recovery", "vessel-build-cost", "route-delivery",
)

# Contract-guid set transitions (report-only facet, design ~439).
CONTRACT_ACCEPT_KIND = "contract-accept"
CONTRACT_CLOSE_KINDS: Tuple[str, ...] = ("contract-complete", "contract-fail")

# Manifest-entry provenance (design Terminology ~85). ``gameevents-captured`` is
# RESERVED for M-B3 (in-game event-time subscriber) but the enum accepts it so a
# future entry parses without a format break.
PROVENANCES: Tuple[str, ...] = (
    "seam-declared", "stock-log-captured", "gameevents-captured",
)

# Per-provenance reputation-mode discriminator (design ~120): seam-declared values
# are `nominal` (pre-curve; the oracle applies the curve), gameevents-captured
# old/new deltas are `applied` (post-curve; added with NO further curve pass, so a
# future applied delta is never double-curved -- the 15.1 distortion, read backward).
REP_MODE_NOMINAL = "nominal"
REP_MODE_APPLIED = "applied"
REP_MODES: Tuple[str, ...] = (REP_MODE_NOMINAL, REP_MODE_APPLIED)

# Every captured amount must be a DELTA (design Mental Model residual blind spot
# ~186 / Behavior ~398): a stock line reporting a post-grant running BALANCE is
# inadmissible (it would double-count against the seed). `amountKind` defaults to
# `delta`; anything else is rejected at parse.
AMOUNT_KIND_DELTA = "delta"
AMOUNT_KIND_BALANCE = "balance"

# Reputation author-constant magnitude cap (M-A5 integration item 9). apply_rep_curve
# splits a nominal rep delta into abs(nominal) integer-sized curve steps, so a huge
# FINITE author constant (e.g. 1e12) would spin the harness through ~1e12 loop
# iterations in-process (an effective hang) before any verdict. The rep pool range is
# +-1000, so a per-event delta beyond +-10000 is already nonsensical; reject it at
# PARSE with a precise reason rather than letting it reach the curve. NaN/Inf are
# rejected separately by _facet_state (non-finite raises).
MAX_REP_AUTHOR_CONSTANT = 10000.0

# STATE-DEPENDENT facets: the stock magnitude is a function of Parsek-PATCHED state
# (per-subject science diminishing returns; current reputation feeding the rep
# curve), so an author constant is REQUIRED and fill-from-capture is FORBIDDEN
# (design Mental Model ~193 / edge 18). A null amount on one of these is a
# scenario-authoring defect. `funds` is state-INDEPENDENT (fill-from-capture
# admissible for an unambiguous single match).
STATE_DEPENDENT_FACETS: Tuple[str, ...] = ("science", "reputation")

# Facet policy MIRRORED (not called) from LedgerGroundTruthDiff (design ~134): the
# scalar pools are HARD (a drift reds PARSEK-FAIL(ledger)); per-identity facets are
# REPORT-ONLY (logged, never red). The oracle reuses the SPLIT, not the C# code.
HARD_FACETS: Tuple[str, ...] = ("funds", "sciencePool", "reputation")

# The ledgerOracle verifier-row statuses (design ~478). PASS / FAIL come from the
# diff here; INVALID / SKIPPED are decided by run.py (tooling-missing edge 13,
# KILLED edge 11, no-ledger-block-declared edge 12) and passed as a status override.
ORACLE_STATUS_PASS = "PASS"
ORACLE_STATUS_FAIL = "FAIL"
ORACLE_STATUS_SKIPPED = "SKIPPED"
ORACLE_STATUS_INVALID = "INVALID"


# ---------------------------------------------------------------------------
# Tolerances (design ~235: "default" mirrors FacetTolerances.Default).
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class Tolerances:
    """Per-facet numeric tolerances (design ~235 / edge 7). Mirrors
    ``FacetTolerances.Default`` (``LedgerGroundTruth.cs``): funds 1.0, science 0.1,
    rep 0.1, subject 0.1. ``vessel`` is the default resource tolerance (0.1 unit
    absolute, physics settling noise, design ~519), tunable per resource in the
    ``[expectations.world]`` block. All comparisons are INCLUSIVE (``<= tol``)."""
    funds: float = 1.0
    science: float = 0.1
    reputation: float = 0.1
    subject: float = 0.1
    vessel: float = 0.1


def default_tolerances() -> Tolerances:
    """The ``tolerances = "default"`` mirror (design ~235)."""
    return Tolerances()


# ---------------------------------------------------------------------------
# Data types (design "Oracle expected-state + diff types" ~330). Field names
# follow the design exactly; additive fields (ut_windows, rec3_*) never rename.
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class SeedBaseline:
    """The fixture's starting career totals (design Terminology ~92). Acquired by
    the analyzer subprocess over the STAGED template save pre-launch, so ONE parser
    produces both the seed and the produced-save totals (a parser drift can never
    desync the legs). A facet is ABSENT (Sandbox / Science) when its value is None;
    ``has_*`` derives presence, mirroring ``CareerSaveSnapshot.HasFunds`` etc."""
    funds: Optional[float]
    science: Optional[float]
    reputation: Optional[float]

    @property
    def has_funds(self) -> bool:
        return self.funds is not None

    @property
    def has_science(self) -> bool:
        return self.science is not None

    @property
    def has_rep(self) -> bool:
        return self.reputation is not None


@dataclass(frozen=True)
class ManifestEntry:
    """One accumulated-manifest entry (design ~338). ``ut`` is null for a captured
    line with no UT-stamped ``[Parsek]`` neighbor; ``seq`` is the ordinal seqKey used
    for the (ut, seq) sort. ``rep_mode`` dispatches the reputation accumulation.
    ``rec3_row`` (additive) carries the route-row identity a non-rewind discard would
    roll back, consulted against the Rec-3 whitelist."""
    ut: Optional[float]
    seq: int
    kind: str
    funds: float
    science: float
    reputation: float
    rep_mode: str
    subject_ids: Tuple[str, ...]
    contract_guid: str
    provenance: str
    rec3_row: str = ""

    @property
    def seq_key(self):
        """The dedupe / sort seqKey, TYPE-TAGGED so a null-UT ordinal can never collide
        with a UT value (design ~394 / edge 2): ``("ut", <float>)`` when ``ut`` is
        known, ``("ord", <int>)`` (the wall-clock ordinal ``seq``) when null. Untagged,
        ordinal ``seq``=3 (int) and captured ``ut``=3.0 (float) compare EQUAL in Python
        (3 == 3.0, hash-equal in dict/set keys), so an award at UT 3.0 would spuriously
        match a null-UT entry ordinal 3 in the fill / unmatched matchers. Must stay in
        lockstep with hlib.CapturedAward.seq_key (they are compared across types)."""
        return ("ut", self.ut) if self.ut is not None else ("ord", self.seq)


@dataclass(frozen=True)
class ManifestParse:
    """Result of parsing a raw entry array: the valid entries + every rejection
    reason (all of them, not just the first, so a scenario author sees the whole
    problem set -- mirrors ``hlib.SpecValidation``)."""
    entries: Tuple[ManifestEntry, ...]
    errors: Tuple[str, ...]

    @property
    def ok(self) -> bool:
        return len(self.errors) == 0


@dataclass(frozen=True)
class ExpectedCareer:
    """Expected career totals (design ~347). ``funds`` / ``science`` / ``reputation``
    are None when the facet is absent (Sandbox). ``ut_windows`` (additive) maps a
    facet name to the bounding manifest UTs that contributed to it, so a drift can
    name WHERE it entered; ``rec3_residual_rows`` (additive) lists route rows the
    carve-out retained (the oracle expects a ``[Rec-3 residual]`` diagnostic for
    each, not a rollback-to-zero)."""
    funds: Optional[float]
    science: Optional[float]
    reputation: Optional[float]
    subject_science: Dict[str, float]
    active_contract_guids: Tuple[str, ...]
    ut_windows: Dict[str, Tuple[Optional[float], Optional[float]]] = field(default_factory=dict)
    rec3_residual_rows: Tuple[str, ...] = ()


@dataclass(frozen=True)
class OracleDivergence:
    """One expected-vs-parsed divergence (design ~354). ``hard`` carries the facet
    policy: scalar pools are hard (a hard divergence reds PARSEK-FAIL(ledger)),
    per-identity facets are report-only. ``expected`` / ``parsed`` are None on the
    absent side of a missing/phantom. ``ut_window`` bounds the drift (``(None, None)``
    for a pool residual with no bracketing entry)."""
    facet: str              # "funds"|"sciencePool"|"reputation"|"subjectScience"|"contract"|"vessel"
    kind: str               # "value-mismatch"|"phantom"|"missing"|"residual-expected"
    identity: str           # subjectId/contractGuid/vesselPid; "" for scalar pools
    expected: Optional[float]
    parsed: Optional[float]
    ut_window: Tuple[Optional[float], Optional[float]]
    hard: bool
    detail: str


# ---------------------------------------------------------------------------
# Seed baseline parse (design Terminology ~92 / Data Model ~272 / edge 15).
# ---------------------------------------------------------------------------


def parse_seed_baseline(seed: Optional[Dict]) -> SeedBaseline:
    """Parse a ``careerSave`` block into a ``SeedBaseline``.

    SINGLE SEED CONTRACT: the seed's one true source is the analyzer's
    ``careerSave`` block over the STAGED template (run.py ``_capture_seed_baseline``
    feeds exactly that block, and the ``<runId>.manifest.json`` ``seed`` audit copy
    is written in the SAME shape). That block is produced by ``ReportWriter`` /
    ``CareerSaveParser``, whose keys are ``funds`` / ``sciencePool`` / ``reputation``
    with the ``hasFunds`` / ``hasScience`` / ``hasRep`` presence flags. The science
    pool key is ``sciencePool`` (NOT ``science`` -- ``science`` is the per-entry
    MANIFEST-ENTRY facet key, a different contract read by ``parse_manifest_entries``).
    A rename on either leg is caught by the cross-lane test that drives one literal
    block through both this parser and ``hlib.parse_career_save_block``.

    A facet is ABSENT (value None) when its ``has<Facet>`` flag is false (Sandbox
    has no funds/science/rep; Science mode has science only) or the block carries no
    numeric value for it. The oracle then skips the absent pool entirely
    (facet-skip-when-absent, design ~442). A missing block -> every facet absent; the
    CALLER (run.py) distinguishes ``parsed=false`` / all-``hasX``-false (INVALID, edge
    15) from a real Sandbox seed via the block's own flags, not here.

    FAULT (item 10): a ``has<Facet>`` flag that is TRUE while its value is missing /
    non-numeric / non-finite is a CONTRADICTION (the block claims the pool present but
    gives no usable number). That must SURFACE as a ``ValueError`` (the caller routes it
    to INVALID(tooling)), never silently degrade to absent -- a silent degrade would
    false-Sandbox a career seed and skip a pool the run genuinely has.
    """
    seed = seed or {}

    def _facet(value_key: str, has_key: str) -> Optional[float]:
        # `has<Facet>` explicitly false -> absent. When the flag is present-and-true the
        # value MUST be a finite number; a contradiction raises rather than degrading.
        has_present = has_key in seed
        claimed = bool(seed.get(has_key)) if has_present else None
        if has_present and not claimed:
            return None
        v = seed.get(value_key)
        numeric = not isinstance(v, bool) and isinstance(v, (int, float)) and math.isfinite(float(v))
        if not numeric:
            if claimed:
                raise ValueError(
                    "careerSave.%s: %s is true but the value %r is not a finite number"
                    % (value_key, has_key, v))
            return None
        return float(v)

    return SeedBaseline(
        funds=_facet("funds", "hasFunds"),
        science=_facet("sciencePool", "hasScience"),
        reputation=_facet("reputation", "hasRep"),
    )


# ---------------------------------------------------------------------------
# Manifest entry parse + validation (design Behavior "Manifest capture" ~368 /
# edge 18; Test Plan "Manifest parse + capture" ~781, PARSE side).
# ---------------------------------------------------------------------------


def _facet_state(raw: Dict, *keys: str) -> Tuple[Optional[float], bool]:
    """Read a facet amount from a raw entry under the first present key.

    Returns ``(value, is_fill)``: a MISSING key -> ``(0.0, False)`` (no delta on
    that facet); an explicit ``null`` (JSON null / Python None) -> ``(None, True)``
    (fill-from-capture requested); a number -> ``(float, False)``. A non-finite
    number raises ``ValueError`` so a NaN/Inf author amount is rejected with a
    precise reason (never silently summed)."""
    for k in keys:
        if k in raw:
            v = raw[k]
            if v is None:
                return None, True
            if isinstance(v, bool) or not isinstance(v, (int, float)):
                raise ValueError("expected a number or null, got %r" % (v,))
            fv = float(v)
            if not math.isfinite(fv):
                raise ValueError("non-finite amount %r" % (fv,))
            return fv, False
    return 0.0, False


def parse_manifest_entries(
    raw_entries: Optional[Sequence[Dict]],
    captured: Optional[Sequence[ManifestEntry]] = None,
) -> ManifestParse:
    """Parse a raw manifest-entry array into ``ManifestEntry`` list + reject reasons.

    Accepts BOTH the spec-declared ``[[expectations.ledger.manifest]]`` array and
    the accumulated ``<runId>.manifest.json`` ``entries`` array (same shape). Every
    malformed entry is rejected with a precise, indexed reason; valid siblings still
    parse (a whole batch is not lost to one bad row). The three load-bearing
    validation rules (design):

      - **Unknown kind rejects** (design ~260 / edge Test Plan ~783): a `kind`
        outside ``KINDS`` is a scenario-authoring defect, not a silently-dropped
        effect.
      - **Every captured amount is a DELTA** (design ~398 / Mental Model ~196): an
        entry flagged ``amountKind = "balance"`` is inadmissible -- a post-grant
        running balance would double-count against the seed.
      - **Author constant REQUIRED for state-dependent facets** (design ~193 /
        edge 18): a null (fill-from-capture) amount on per-subject science or
        reputation is REJECTED outright -- filling it from the stock capture would
        re-copy the possibly-corrupted leg-B magnitude and lose leg A's
        independence. Only the state-INDEPENDENT funds pool may fill from capture,
        and only on EXACTLY ONE matching captured line; zero or multiple matches ->
        flagged ambiguous (an un-fillable declaration is surfaced, not swallowed).

    ``captured`` is the already-parsed pool of ``stock-log-captured`` entries the
    fill draws from (run.py greps + parses them first; None when there is nothing to
    fill from).
    """
    errors: List[str] = []
    entries: List[ManifestEntry] = []
    captured = list(captured or ())

    for i, raw in enumerate(raw_entries or ()):
        if not isinstance(raw, dict):
            errors.append("entry[%d]: not a table/object: %r" % (i, raw))
            continue

        kind = raw.get("kind")
        if kind not in KINDS:
            errors.append("entry[%d].kind: %r not in the kind enum" % (i, kind))
            continue

        provenance = raw.get("provenance", "seam-declared")
        if provenance not in PROVENANCES:
            errors.append("entry[%d].provenance: %r not in %s"
                          % (i, provenance, list(PROVENANCES)))
            continue

        # amountKind: only a per-event DELTA is admissible; a BALANCE is rejected.
        amount_kind = raw.get("amountKind", raw.get("amount_kind", AMOUNT_KIND_DELTA))
        if amount_kind != AMOUNT_KIND_DELTA:
            errors.append(
                "entry[%d].amountKind: %r inadmissible; every captured amount must be a "
                "DELTA (a post-grant BALANCE would double-count against the seed)"
                % (i, amount_kind))
            continue

        try:
            funds, funds_fill = _facet_state(raw, "funds")
            science, science_fill = _facet_state(raw, "science")
            reputation, rep_fill = _facet_state(raw, "reputation")
        except ValueError as ex:
            errors.append("entry[%d]: %s" % (i, ex))
            continue

        # State-dependent facets MUST carry an author constant; a null there is a
        # scenario-authoring defect (fill-from-capture forbidden, design edge 18).
        if science_fill:
            errors.append(
                "entry[%d].science: null amount on the state-dependent per-subject "
                "science facet; an author constant is required (fill-from-capture "
                "forbidden)" % (i,))
            continue
        if rep_fill:
            errors.append(
                "entry[%d].reputation: null amount on the state-dependent reputation "
                "facet; an author constant is required (fill-from-capture forbidden)"
                % (i,))
            continue
        # Bound the rep author constant so a huge finite magnitude cannot hang the
        # harness inside apply_rep_curve's per-integer-step loop (item 9). funds/science
        # do not loop, so only reputation needs the cap.
        if abs(reputation) > MAX_REP_AUTHOR_CONSTANT:
            errors.append(
                "entry[%d].reputation: author constant %r exceeds the +-%g magnitude "
                "cap (a per-event rep delta this large is nonsensical and would hang the "
                "curve step loop)" % (i, reputation, MAX_REP_AUTHOR_CONSTANT))
            continue

        rep_mode = raw.get("repMode", raw.get("rep_mode"))
        if rep_mode is None:
            # Default by provenance: seam-declared values are pre-curve (nominal);
            # gameevents-captured old/new deltas are post-curve (applied).
            rep_mode = REP_MODE_APPLIED if provenance == "gameevents-captured" else REP_MODE_NOMINAL
        if rep_mode not in REP_MODES:
            errors.append("entry[%d].repMode: %r not in %s" % (i, rep_mode, list(REP_MODES)))
            continue

        ut = raw.get("ut")
        if ut is not None:
            if isinstance(ut, bool) or not isinstance(ut, (int, float)) or not math.isfinite(float(ut)):
                errors.append("entry[%d].ut: %r must be a finite number or null" % (i, ut))
                continue
            ut = float(ut)
        seq = raw.get("seq", i)
        if isinstance(seq, bool) or not isinstance(seq, int):
            errors.append("entry[%d].seq: %r must be an int ordinal" % (i, seq))
            continue

        subject_ids = tuple(str(s) for s in (raw.get("subjectIds", raw.get("subject_ids", [])) or []))
        contract_guid = str(raw.get("contractGuid", raw.get("contract_guid", "")) or "")
        rec3_row = str(raw.get("rec3Row", raw.get("rec3_row", "")) or "")

        # funds is state-INDEPENDENT: a null amount fills from an unambiguous single
        # captured line matching (seqKey, kind, contractGuid, FUNDS-FACET). The facet
        # filter (``c.funds != 0.0``) is load-bearing: a single stock contract-complete
        # emits a funds award AND a reputation award at the SAME (kind, guid, seqKey),
        # so without it BOTH captured entries match and the fill fails ambiguous even
        # though exactly one carries the funds delta being filled (still fail-closed on
        # zero or genuine multiple funds matches).
        if funds_fill:
            # TYPE-TAGGED key (must mirror ManifestEntry.seq_key) so a null-UT ordinal
            # cannot collide with a captured award's UT value (edge 8).
            seq_key = ("ut", ut) if ut is not None else ("ord", seq)
            matches = [
                c for c in captured
                if c.kind == kind and c.contract_guid == contract_guid
                and c.seq_key == seq_key and c.funds != 0.0
            ]
            if len(matches) != 1:
                errors.append(
                    "entry[%d].funds: null fill-from-capture is ambiguous (%d matching "
                    "captured funds lines for kind=%s contractGuid=%s seqKey=%r; need exactly 1)"
                    % (i, len(matches), kind, contract_guid or "(none)", seq_key))
                continue
            funds = matches[0].funds

        entries.append(ManifestEntry(
            ut=ut, seq=seq, kind=kind,
            funds=funds, science=science, reputation=reputation,
            rep_mode=rep_mode, subject_ids=subject_ids,
            contract_guid=contract_guid, provenance=provenance, rec3_row=rec3_row,
        ))

    return ManifestParse(tuple(entries), tuple(errors))


# ---------------------------------------------------------------------------
# Reputation curve (design ~118 / ~430; port of ReputationModule.ApplyReputationCurve
# -- decompiled stock keyframes, Unity Hermite tangent evaluation, Spike A).
# ---------------------------------------------------------------------------

_REP_RANGE = 1000.0

# reputationAddition keyframes (5 keys) -- gain curve, diminishes at high rep.
_ADD_TIMES = (-1.000108, -0.505605, 0.001540, 0.501354, 1.000023)
_ADD_VALUES = (2.001723, 1.500368, 0.999268, 0.503444, -0.000005)
_ADD_IN_SLOPES = (0.873274, -2.772799, 0.009784, -2.572293, -0.006748)
_ADD_OUT_SLOPES = (-0.025381, -2.772799, 0.009784, -2.572293, 1.003260)

# reputationSubtraction keyframes (4 keys) -- loss curve, amplified at high rep.
_SUB_TIMES = (-1.000136, -1.000038, -0.000005, 1.000356)
_SUB_VALUES = (-0.000129, 0.049983, 1.000065, 1.998481)
_SUB_IN_SLOPES = (-1216.706, 2.479460, 0.950051, 0.998054)
_SUB_OUT_SLOPES = (510.160, 0.950051, 0.998054, 0.949444)


def _cubic_hermite(t: float, p0: float, m0: float, p1: float, m1: float) -> float:
    """Standard cubic Hermite interpolation (mirrors ReputationModule.CubicHermite).
    ``t`` in [0,1]; ``p0``/``p1`` endpoint values; ``m0``/``m1`` endpoint tangents."""
    t2 = t * t
    t3 = t2 * t
    h00 = 2.0 * t3 - 3.0 * t2 + 1.0
    h10 = t3 - 2.0 * t2 + t
    h01 = -2.0 * t3 + 3.0 * t2
    h11 = t3 - t2
    return h00 * p0 + h10 * m0 + h01 * p1 + h11 * m1


def _evaluate_hermite(time: float, times, values, in_slopes, out_slopes) -> float:
    """Evaluate a cubic Hermite spline defined by keyframes, clamping to the boundary
    values outside the key range (mirrors ReputationModule.EvaluateHermiteSpline;
    Unity AnimationCurve uses the outSlope of the left key + the inSlope of the right,
    tangents scaled by the segment duration)."""
    count = len(times)
    if count == 0:
        return 0.0
    if time <= times[0]:
        return values[0]
    if time >= times[count - 1]:
        return values[count - 1]
    left = 0
    for i in range(count - 1):
        if time < times[i + 1]:
            left = i
            break
    right = left + 1
    dt = times[right] - times[left]
    if dt <= 0.0:
        return values[left]
    t = (time - times[left]) / dt
    m0 = out_slopes[left] * dt
    m1 = in_slopes[right] * dt
    return _cubic_hermite(t, values[left], m0, values[right], m1)


def _evaluate_addition_curve(time: float) -> float:
    return _evaluate_hermite(time, _ADD_TIMES, _ADD_VALUES, _ADD_IN_SLOPES, _ADD_OUT_SLOPES)


def _evaluate_subtraction_curve(time: float) -> float:
    return _evaluate_hermite(time, _SUB_TIMES, _SUB_VALUES, _SUB_IN_SLOPES, _SUB_OUT_SLOPES)


def apply_rep_curve(nominal: float, current_rep: float,
                    rep_range: float = _REP_RANGE) -> Tuple[float, float]:
    """Apply KSP's non-linear reputation curve to a nominal delta at a running rep.

    Port of ``ReputationModule.ApplyReputationCurve`` (the SetReputation-semantics
    exception, plan 6 / game-actions 15.1). Splits ``nominal`` into integer-sized
    steps; each step is scaled by the gain curve (positive) or loss curve (negative)
    evaluated at ``rep / rep_range``, so the effective delta is NOT a linear function
    of ``nominal`` -- summing nominal rep deltas linearly is the double-curve
    distortion 15.1 warns of. Returns ``(actual_delta, new_rep)``.

    RESIDUAL BLIND SPOT (design ~899): this is a PORT of the same keyframes Parsek's
    C# rep module uses. If BOTH share an identical transcription fault, the oracle's
    expected, the produced save, and a Parsek recalc all agree on the WRONG number
    (expected == save == recalc), so the diff stays green -- the two legs are only
    independent up to a shared-curve error. The value-pinned tests anchor this port to
    ABSOLUTE magnitudes (not the port composed against itself), so a keyframe typo reds
    a test; the remaining deferred anchor is a check against KNOWN IN-GAME rep
    transitions before any L1 rep script trusts a non-empty rep manifest.
    """
    if nominal == 0.0:
        return 0.0, current_rep
    num = int(abs(nominal))
    delta = math.copysign(1.0, nominal)
    accumulated = 0.0
    rep = current_rep
    for i in range(num + 1):
        step_input = delta if i != num else (nominal - (delta * num))
        if step_input == 0.0:
            continue
        time = rep / rep_range
        mult = _evaluate_subtraction_curve(time) if step_input < 0.0 else _evaluate_addition_curve(time)
        step = step_input * mult
        rep += step
        accumulated += step
    return accumulated, rep


# ---------------------------------------------------------------------------
# Oracle computation (design "Oracle computation" ~412).
# ---------------------------------------------------------------------------


def _sort_key(e: ManifestEntry):
    """Deterministic accumulation order (design ~410 / edge 9): sort by (ut, seq)
    with null-UT entries ordered after UT-stamped ones by their wall-clock ordinal.
    UT is NEVER used as a prune/cutoff bound (the cold-load-UT=0 trap, edge 9); it
    only orders."""
    return (e.ut is None, e.ut if e.ut is not None else 0.0, e.seq)


def _window(contributing: Sequence[ManifestEntry]) -> Tuple[Optional[float], Optional[float]]:
    """Bounding (lo, hi) manifest UTs for a facet's contributing entries; ``(None,
    None)`` when nothing UT-stamped contributed (a pool residual with no bracketing
    entry)."""
    uts = [e.ut for e in contributing if e.ut is not None]
    if not uts:
        return (None, None)
    return (min(uts), max(uts))


def compute_expected(
    seed: SeedBaseline,
    entries: Sequence[ManifestEntry],
    tolerances: Optional[Tolerances] = None,
    rec3_whitelist: Optional[Sequence[str]] = None,
) -> ExpectedCareer:
    """Turn a seed baseline + accumulated manifest into expected career totals (pure).

    Per-facet math (design ~416):
      - **Funds (HARD)**: ``seed.funds + sum(e.funds)``. On a non-rewind discard the
        Rec-3 carve-out SUPPRESSES the rollback of a whitelisted route row (the
        residual persists by design, plan 6 / logistics 10.6); the oracle records
        the row in ``rec3_residual_rows`` and expects a ``[Rec-3 residual]``
        diagnostic instead of a rollback-to-zero. A non-whitelisted row rolls back.
      - **Science pool (HARD)**: ``seed.science + sum(e.science)`` -- the captured
        (post-cap) deltas, never a cap recomputation.
      - **Reputation (HARD, curve exception)**: NOT a linear sum -- accumulated
        sequentially from ``seed.reputation``, each ``nominal`` entry run through
        ``apply_rep_curve`` at the running rep, each ``applied`` entry added directly
        (no second curve pass).
      - **Per-subject science (REPORT-ONLY)**: ``e.science`` onto ``e.subjectIds``.
      - **Active contracts (REPORT-ONLY)**: accept adds, complete/fail remove.
      - **Facet absence**: an absent seed pool (Sandbox) stays None and the diff
        skips it.

    ``rec3_whitelist`` is the collection of route-row identities the carve-out
    retains (empty / None for a plain scenario like B10).
    """
    tolerances = tolerances or default_tolerances()  # accepted for symmetry with diff; math is exact
    whitelist = set(rec3_whitelist or ())
    ordered = sorted(entries, key=_sort_key)

    ut_windows: Dict[str, Tuple[Optional[float], Optional[float]]] = {}

    # --- Funds pool (HARD) + Rec-3 carve-out.
    funds: Optional[float] = None
    residual_rows: List[str] = []
    if seed.has_funds:
        funds = seed.funds
        contributing_funds: List[ManifestEntry] = []
        for e in ordered:
            if e.funds == 0.0:
                continue
            if e.rec3_row and e.rec3_row in whitelist:
                # Ratified residual: the route row does NOT roll back; expect the
                # [Rec-3 residual] diagnostic line, not a rollback-to-zero.
                if e.rec3_row not in residual_rows:
                    residual_rows.append(e.rec3_row)
                continue
            funds += e.funds
            contributing_funds.append(e)
        ut_windows["funds"] = _window(contributing_funds)

    # --- Science pool (HARD).
    science: Optional[float] = None
    if seed.has_science:
        contributing_sci = [e for e in ordered if e.science != 0.0]
        science = seed.science + sum(e.science for e in contributing_sci)
        ut_windows["sciencePool"] = _window(contributing_sci)

    # --- Reputation (HARD, curve exception).
    reputation: Optional[float] = None
    if seed.has_rep:
        running = seed.reputation
        contributing_rep: List[ManifestEntry] = []
        for e in ordered:
            if e.reputation == 0.0:
                continue
            if e.rep_mode == REP_MODE_APPLIED:
                running += e.reputation  # already post-curve; no second pass.
            else:
                _delta, running = apply_rep_curve(e.reputation, running)
            contributing_rep.append(e)
        reputation = running
        ut_windows["reputation"] = _window(contributing_rep)

    # --- Per-subject science (REPORT-ONLY).
    subject_science: Dict[str, float] = {}
    contributing_subj: List[ManifestEntry] = []
    for e in ordered:
        if not e.subject_ids:
            continue
        for sid in e.subject_ids:
            subject_science[sid] = subject_science.get(sid, 0.0) + e.science
        contributing_subj.append(e)
    if contributing_subj:
        ut_windows["subjectScience"] = _window(contributing_subj)

    # --- Active contracts (REPORT-ONLY).
    active: List[str] = []
    contributing_contract: List[ManifestEntry] = []
    for e in ordered:
        if e.kind == CONTRACT_ACCEPT_KIND:
            if e.contract_guid and e.contract_guid not in active:
                active.append(e.contract_guid)
            contributing_contract.append(e)
        elif e.kind in CONTRACT_CLOSE_KINDS:
            if e.contract_guid in active:
                active.remove(e.contract_guid)
            contributing_contract.append(e)
    if contributing_contract:
        ut_windows["contract"] = _window(contributing_contract)

    return ExpectedCareer(
        funds=funds, science=science, reputation=reputation,
        subject_science=subject_science, active_contract_guids=tuple(active),
        ut_windows=ut_windows, rec3_residual_rows=tuple(residual_rows),
    )


# ---------------------------------------------------------------------------
# Expected-vs-parsed diff (design "The ledger-oracle verifier" ~444 / Diff facet
# policy Test Plan ~801). Mirrors LedgerGroundTruthDiff's hard-vs-report-only split.
# ---------------------------------------------------------------------------


def _finite(x: Optional[float]) -> bool:
    return isinstance(x, (int, float)) and not isinstance(x, bool) and math.isfinite(float(x))


def within_tolerance(expected: Optional[float], parsed: Optional[float], tol: float) -> bool:
    """Inclusive tolerance check (design edge 7): ``abs(expected - parsed) <= tol``.

    A NaN or Inf on EITHER side NEVER passes (RewindReadbackGuard semantics, design
    ~432): a non-finite career value is always a divergence, never absorbed by the
    tolerance. A None on either side (facet presence mismatch) never passes here;
    the caller decides missing-vs-skip."""
    if not _finite(expected) or not _finite(parsed):
        return False
    return abs(float(expected) - float(parsed)) <= tol


def _career_block_facet(career_save: Dict, has_key: str, value_key: str) -> Tuple[bool, Optional[float]]:
    """Read a parsed careerSave pool: ``(present, value)``. Present iff the block's
    ``hasX`` flag is true AND a numeric value is carried."""
    present = bool(career_save.get(has_key, False))
    v = career_save.get(value_key)
    if not (isinstance(v, (int, float)) and not isinstance(v, bool)):
        return present, None
    return present, float(v)


def _diff_pool(facet: str, expected: Optional[float], present: bool, parsed: Optional[float],
               tol: float, window: Tuple[Optional[float], Optional[float]]) -> Optional[OracleDivergence]:
    """Diff one HARD scalar pool. Facet-skip-when-absent: when the expected pool is
    absent (Sandbox seed) there is nothing to assert. When expected is present but
    the parsed block lacks the pool -> a hard ``missing``; else an inclusive
    tolerance compare -> ``value-mismatch`` on drift."""
    if expected is None:
        return None  # facet absent from the seed -> skip (design ~442).
    if not present or parsed is None:
        return OracleDivergence(
            facet=facet, kind="missing", identity="", expected=expected, parsed=None,
            ut_window=window, hard=True,
            detail="expected %s pool %r but the produced save carries no %s facet"
                   % (facet, expected, facet))
    if not within_tolerance(expected, parsed, tol):
        return OracleDivergence(
            facet=facet, kind="value-mismatch", identity="", expected=expected, parsed=parsed,
            ut_window=window, hard=True,
            detail="%s expected=%r parsed=%r delta=%r tol=%r"
                   % (facet, expected, parsed, float(parsed) - float(expected), tol))
    return None


def diff_expected_vs_parsed(
    expected: ExpectedCareer,
    career_save: Dict,
    tolerances: Optional[Tolerances] = None,
    rec3_whitelist: Optional[Sequence[str]] = None,
) -> List[OracleDivergence]:
    """Diff the expected career totals against the parsed ``careerSave`` block (pure).

    Mirrors the ``LedgerGroundTruthDiff`` FACET POLICY (design ~134 / ~469): the
    scalar pools (funds / science / reputation) are HARD (a divergence reds
    PARSEK-FAIL(ledger)); per-subject science and contract-guid transitions are
    REPORT-ONLY (recorded, never red). Each divergence carries its bounding manifest
    UT window. The ``UpliftClampedExpected`` Layer-B carve-out does NOT transfer
    (design ~140): the manifest oracle asserts the stock-granted amount against the
    save directly, no uplift-clamp exemption.

    ``career_save`` is the parsed ``.analysis.json`` ``careerSave`` block
    (``hlib.parse_career_save_block`` output, run.py-side): ``hasFunds`` / ``funds``
    / ``hasScience`` / ``sciencePool`` / ``hasRep`` / ``reputation`` /
    ``subjectScience`` / ``activeContractGuids``.
    """
    tolerances = tolerances or default_tolerances()
    divergences: List[OracleDivergence] = []

    # --- HARD scalar pools.
    f_present, f_parsed = _career_block_facet(career_save, "hasFunds", "funds")
    d = _diff_pool("funds", expected.funds, f_present, f_parsed,
                   tolerances.funds, expected.ut_windows.get("funds", (None, None)))
    if d is not None:
        divergences.append(d)

    s_present, s_parsed = _career_block_facet(career_save, "hasScience", "sciencePool")
    d = _diff_pool("sciencePool", expected.science, s_present, s_parsed,
                   tolerances.science, expected.ut_windows.get("sciencePool", (None, None)))
    if d is not None:
        divergences.append(d)

    r_present, r_parsed = _career_block_facet(career_save, "hasRep", "reputation")
    d = _diff_pool("reputation", expected.reputation, r_present, r_parsed,
                   tolerances.reputation, expected.ut_windows.get("reputation", (None, None)))
    if d is not None:
        divergences.append(d)

    # --- Per-subject science (REPORT-ONLY). A per-identity mixed-history difference
    # is exactly the false-positive LedgerGroundTruthDiff avoids -> hard=False.
    subj_window = expected.ut_windows.get("subjectScience", (None, None))
    parsed_subj = career_save.get("subjectScience", {}) or {}
    for sid in sorted(set(expected.subject_science) | set(parsed_subj)):
        e_val = expected.subject_science.get(sid)
        p_raw = parsed_subj.get(sid)
        p_val = float(p_raw) if isinstance(p_raw, (int, float)) and not isinstance(p_raw, bool) else None
        if e_val is None:
            divergences.append(OracleDivergence(
                "subjectScience", "phantom", sid, None, p_val, subj_window, False,
                "subject %s present in the save but not expected" % sid))
        elif p_val is None:
            divergences.append(OracleDivergence(
                "subjectScience", "missing", sid, e_val, None, subj_window, False,
                "subject %s expected but absent from the save" % sid))
        elif not within_tolerance(e_val, p_val, tolerances.subject):
            divergences.append(OracleDivergence(
                "subjectScience", "value-mismatch", sid, e_val, p_val, subj_window, False,
                "subject %s expected=%r parsed=%r tol=%r" % (sid, e_val, p_val, tolerances.subject)))

    # --- Active contracts (REPORT-ONLY): transitions logged, not red (design edge 14).
    con_window = expected.ut_windows.get("contract", (None, None))
    parsed_guids = set(str(g) for g in (career_save.get("activeContractGuids", []) or []))
    expected_guids = set(expected.active_contract_guids)
    for g in sorted(expected_guids - parsed_guids):
        divergences.append(OracleDivergence(
            "contract", "missing", g, None, None, con_window, False,
            "contract %s expected active but not in the save" % g))
    for g in sorted(parsed_guids - expected_guids):
        divergences.append(OracleDivergence(
            "contract", "phantom", g, None, None, con_window, False,
            "contract %s active in the save but not expected" % g))

    # --- Rec-3 ratified residual (REPORT-ONLY): the oracle expects a diagnostic, not
    # a rollback; surfaced so the verifier can log the retained row (design edge 8).
    for row in expected.rec3_residual_rows:
        divergences.append(OracleDivergence(
            "funds", "residual-expected", row, None, None,
            expected.ut_windows.get("funds", (None, None)), False,
            "Rec-3 residual retained row=%s (expect a [Rec-3 residual] diagnostic, "
            "not a rollback-to-zero)" % row))

    return divergences


# ---------------------------------------------------------------------------
# World vessel-resource diff (design "expectations.world activation" ~491 / Test
# Plan "World vessel resource correlation" ~811).
# ---------------------------------------------------------------------------


def _vessel_guid(v: Dict) -> str:
    return str(v.get("guid", v.get("pid", "")) or "")


def _vessel_pid(v: Dict):
    return v.get("persistentId")


def diff_world_vessels(
    declared: Optional[Sequence[Dict]],
    parsed_vessels: Optional[Sequence[Dict]],
    tolerances: Optional[Tolerances] = None,
    report_phantoms: bool = False,
) -> List[OracleDivergence]:
    """Diff declared vessel resource totals against the parsed ``careerSave.vessels``.

    Correlation (design ~508): launch guid PREFERRED, then persistentId. A
    guid-correlated resource drift is HARD (``PARSEK-FAIL(ledger)``); a pid-ONLY
    correlation is REPORT-ONLY (the craft-baked-pid caveat -- a bare persistentId is
    not launch-unique, mirroring ``LedgerGroundTruthDiff.CompareRecovery``). A
    declared vessel absent from the save -> ``missing`` (hard iff guid-declared); a
    resource outside ``expected +/- tol`` -> ``value-mismatch``. ``report_phantoms``
    emits a report-only ``phantom`` for each parsed vessel not declared (off by
    default: the world block is a resource whitelist, not an exhaustive census).

    ``declared`` entries: ``{guid?, persistentId?, resources: {name: {expected, tol?}}}``.
    ``parsed_vessels`` entries: ``{pid|guid, persistentId, resourceTotals: {name: amt}}``.
    """
    tolerances = tolerances or default_tolerances()
    divergences: List[OracleDivergence] = []
    parsed_vessels = list(parsed_vessels or ())

    by_guid = {_vessel_guid(v): v for v in parsed_vessels if _vessel_guid(v)}
    by_pid = {}
    for v in parsed_vessels:
        pid = _vessel_pid(v)
        if pid is not None:
            by_pid.setdefault(pid, v)

    matched_guids: set = set()

    for entry in (declared or ()):
        guid = str(entry.get("guid", "") or "")
        pid = entry.get("persistentId")
        resources = entry.get("resources", {}) or {}

        match = None
        guid_correlated = False
        identity = guid or (str(pid) if pid is not None else "")
        if guid and guid in by_guid:
            match = by_guid[guid]
            guid_correlated = True
            matched_guids.add(guid)
        elif pid is not None and pid in by_pid:
            match = by_pid[pid]
            guid_correlated = False  # pid-only -> report-only (craft-baked-pid caveat).
            mg = _vessel_guid(match)
            if mg:
                matched_guids.add(mg)

        if match is None:
            # A guid-declared miss is hard; a pid-only-declared miss is report-only.
            divergences.append(OracleDivergence(
                "vessel", "missing", identity, None, None, (None, None), bool(guid),
                "declared vessel %s absent from the produced save" % (identity or "(unnamed)")))
            continue

        totals = match.get("resourceTotals", {}) or {}
        for res_name in sorted(resources):
            spec = resources[res_name] or {}
            exp = spec.get("expected")
            tol = spec.get("tol", tolerances.vessel)
            parsed_amt = totals.get(res_name)
            p_val = float(parsed_amt) if isinstance(parsed_amt, (int, float)) and not isinstance(parsed_amt, bool) else None
            e_val = float(exp) if isinstance(exp, (int, float)) and not isinstance(exp, bool) else None
            if e_val is None:
                continue
            if not within_tolerance(e_val, p_val, tol):
                divergences.append(OracleDivergence(
                    "vessel", "value-mismatch", identity, e_val, p_val, (None, None),
                    guid_correlated,
                    "vessel %s resource %s expected=%r parsed=%r tol=%r%s"
                    % (identity, res_name, e_val, p_val, tol,
                       "" if guid_correlated else " (pid-only correlation, report-only)")))

    if report_phantoms:
        for v in parsed_vessels:
            g = _vessel_guid(v)
            if g and g not in matched_guids:
                divergences.append(OracleDivergence(
                    "vessel", "phantom", g, None, None, (None, None), False,
                    "vessel %s present in the save but not declared" % g))

    return divergences


# ---------------------------------------------------------------------------
# Verifier-row result (design ~478: the ledgerOracle result slot). Deterministic.
# ---------------------------------------------------------------------------


def has_hard_drift(divergences: Sequence[OracleDivergence]) -> bool:
    """True iff any divergence is HARD -- the ``ledger_drift`` boolean the verifier
    feeds ``hlib.classify_verdict`` (which maps it to PARSEK-FAIL(ledger))."""
    return any(d.hard for d in divergences)


def _aggregate_ut_window(hard: Sequence[OracleDivergence]) -> Tuple[Optional[float], Optional[float]]:
    los = [d.ut_window[0] for d in hard if d.ut_window[0] is not None]
    his = [d.ut_window[1] for d in hard if d.ut_window[1] is not None]
    return (min(los) if los else None, max(his) if his else None)


def build_oracle_result(
    divergences: Sequence[OracleDivergence],
    status_override: Optional[str] = None,
    reason: str = "",
) -> Dict:
    """Build the ``ledgerOracle`` verifier-row (design ~478). Fills the slot
    ``run.py`` reserves as ``{"status":"SKIPPED","reason":"no-ledger-block-declared"}``.

    PASS iff there is NO hard divergence; FAIL on any hard divergence, with
    ``utWindow`` = the bounding window across the hard divergences (``[None, None]``
    when no hard divergence carries a window). Report-only divergences are counted
    (``reportOnly``) but never flip the status. ``status_override`` lets the caller
    stamp a SKIPPED / INVALID (KILLED edge 11, tooling-missing edge 13,
    no-ledger-block-declared edge 12) that the diff itself cannot decide.
    """
    hard = [d for d in divergences if d.hard]
    report_only = [d for d in divergences if not d.hard]
    if status_override is not None:
        status = status_override
    else:
        status = ORACLE_STATUS_FAIL if hard else ORACLE_STATUS_PASS
    lo, hi = _aggregate_ut_window(hard)
    result: Dict = {
        "status": status,
        "hardDivergences": len(hard),
        "reportOnly": len(report_only),
        "utWindow": [lo, hi],
    }
    if reason:
        result["reason"] = reason
    return result


def divergence_to_dict(d: OracleDivergence) -> Dict:
    """Serialize one divergence to a stable-keyed dict for the manifest / audit trail."""
    return {
        "facet": d.facet,
        "kind": d.kind,
        "identity": d.identity,
        "expected": d.expected,
        "parsed": d.parsed,
        "utWindow": [d.ut_window[0], d.ut_window[1]],
        "hard": d.hard,
        "detail": d.detail,
    }


def serialize_oracle_result(result: Dict) -> str:
    """Serialize a verifier-row deterministically (stable key order, floats via repr
    through json, explicit ``\\n`` endings), mirroring ``hlib.serialize_result`` so a
    record written on Windows and Linux is byte-identical."""
    text = json.dumps(result, sort_keys=True, indent=2, ensure_ascii=True)
    return text.replace("\r\n", "\n") + "\n"
