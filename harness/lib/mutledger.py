"""Pure core of the ledger-oracle mutation checker (MUTATION-CHECK-PHASE-2, PR 2).

The ledger oracle (M-B2, ``oracle.py``) reds ``PARSEK-FAIL(ledger)`` when the
produced save's career pools drift from the seed plus the declared manifest, when
a declared roster claim fails, or (where a spec arms it) when a stock award line
in the KSP.log is explained by no declared entry. Phase 1 could not reach it: it
needs the run's seed. That seed is archived: ``run.py`` writes the seed's
careerSave-shaped audit copy into ``<runId>.manifest.json``, and the produced
save's ``analysis/<leaf>.analysis.json`` (with the ``careerSave`` block the oracle
diffs) travels with the save snapshot. Given those plus the KSP.log, this module
replays the REAL verifier (``ledgerverify.evaluate``, the same function
``run.py`` calls) over perturbed copies of the inputs:

- the produced pool, moved just past the facet tolerance either side of the
  expected value (``pool``);
- the seed pool, moved so the expected value crosses the tolerance (``seed``);
- each declared manifest amount, moved the same way (``entry``), and each entry
  with a non-zero amount removed outright (``remove``, the award that vanished);
- where ``captureCrossCheck = "gate"``: a stock award line injected into the log
  with an amount no entry explains, and each captured award's amount moved past
  the tolerance (``capture``);
- each ``[expectations.world.roster]`` name dropped from (``present``) or added to
  (``absent``) the produced roster (``roster``);
- faults: a torn analysis file, no careerSave block, ``parsed = false``, no seed,
  an unparseable manifest entry, a missing pool, a missing roster facet.

A kill is attributed, never inferred from the run going red: a pool edit counts
only when a HARD divergence on THAT pool appears, a roster edit only on a hard
divergence naming THAT kerbal, a capture edit only on a hard ``unexpected-award``,
a tooling fault only on the INVALID(tooling) route. Per gate the verdict is:

- ``PROVEN``: every constructed crossing edit was killed (a tolerance has two
  sides; a one-sided compare would leave one survivor);
- ``VACUOUS``: some constructed edit crossed (or tried to) and the gate stayed
  green; the reason names the surviving side;
- ``UNCHECKED``: no edit could be constructed (a pool absent from the seed, an
  amount the curve could not push across within ``MAX_SCALE_STEPS`` doublings, a
  facet this checker does not model).

Edits to the expected side are PLANNED with ``oracle.compute_expected`` (the
amount needed to cross a non-linear reputation curve), but the verdict always
comes from the full ``ledgerverify.evaluate``. A planner that sees the expected
value never move still runs the edit, and a survivor there is VACUOUS (a dead
path), never UNCHECKED, so a broken ``compute_expected`` cannot hide behind the
planner.

Pure: no file I/O, no clock. ``harness/tools/mutation_check.py`` is the shell.
"""

from __future__ import annotations

import copy
import json
from dataclasses import dataclass, field
from typing import Callable, Dict, List, Optional, Sequence, Tuple

import hlib
import ledgerverify
import oracle
from mutsave import PROVEN, UNCHECKED, VACUOUS, GateVerdict

VERIFIER = "ledgerOracle"
KIND_POOL = "ledger-pool"
KIND_SEED = "ledger-seed"
KIND_ENTRY = "ledger-entry"
KIND_REMOVE = "ledger-remove"
KIND_CAPTURE = "ledger-capture"
KIND_ROSTER = "ledger-roster"
KIND_FAULT = "ledger-fault"

# A crossing lands this fraction of the tolerance past it, so float noise can never
# leave an edit sitting exactly on the inclusive bound.
CROSSING_MARGIN = 0.01
# Doublings the planner may apply to an expected-side shift before it gives up
# (only the reputation curve ever needs more than one).
MAX_SCALE_STEPS = 12

# (diff facet, has flag, careerSave value key, tolerance attr, manifest entry key)
POOLS: Tuple[Tuple[str, str, str, str, str], ...] = (
    ("funds", "hasFunds", "funds", "funds", "funds"),
    ("sciencePool", "hasScience", "sciencePool", "science", "science"),
    ("reputation", "hasRep", "reputation", "reputation", "reputation"),
)
ENTRY_KEY_TO_POOL = {p[4]: p for p in POOLS}

INJECTED_REASON = "MutationCheck"
UNKNOWN_KIND = "mutcheck-unknown-kind"
INJECTED_KERBAL = {"name": "", "gender": "Male", "type": "Crew", "trait": "Pilot",
                   "state": "Available"}


# ---------------------------------------------------------------------------
# Inputs + evaluation.
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class LedgerInputs:
    """What the shell read for one archived run. ``seed_block`` is the
    careerSave-shaped seed copy in ``<runId>.manifest.json`` (None when the run
    archived no manifest); ``analysis_text`` the produced save's
    ``.analysis.json``; ``log_text`` the produced KSP.log."""
    seed_block: Optional[Dict]
    analysis_text: Optional[str]
    log_text: str
    # Set when the shell could not read the archive's ledger inputs (OSError); the
    # baseline then reports the archive unreadable instead of the sweep aborting.
    read_error: str = ""


def blocks(expectations: Dict) -> Tuple[Optional[Dict], Optional[Dict]]:
    """(``[expectations.ledger]``, ``[expectations.world]``) or None each."""
    led = expectations.get("ledger")
    world = expectations.get("world")
    return (led if isinstance(led, dict) else None,
            world if isinstance(world, dict) else None)


def is_ledger_spec(expectations: Dict) -> bool:
    led, world = blocks(expectations)
    return led is not None or world is not None


def evaluate(expectations: Dict, seed_block: Optional[Dict], career_block: Optional[Dict],
             log_text: str) -> ledgerverify.LedgerVerdict:
    """The real verifier over one (possibly edited) input set. A seed whose
    ``has<Facet>`` flag contradicts its value is the pre-launch INVALID(tooling)
    ``run.py`` raises; it maps to the same tooling route here."""
    led, world = blocks(expectations)
    seed = None
    if seed_block is not None:
        try:
            seed = oracle.parse_seed_baseline(seed_block)
        except ValueError as exc:
            return ledgerverify.LedgerVerdict(
                {"status": oracle.ORACLE_STATUS_INVALID, "subkind": "tooling",
                 "reason": "seed: %s" % exc, "hardDivergences": 0, "reportOnly": 0,
                 "utWindow": [None, None]}, False, True)
    return ledgerverify.evaluate(led, world, career_block, seed, log_text)


def _passed(v: ledgerverify.LedgerVerdict) -> bool:
    return v.result.get("status") == oracle.ORACLE_STATUS_PASS and not v.tooling


def _hard(v: ledgerverify.LedgerVerdict, pred: Callable) -> bool:
    return any(d.hard and pred(d) for d in v.divergences)


def _pool_red(facet: str) -> Callable[[ledgerverify.LedgerVerdict], bool]:
    return lambda v: _hard(v, lambda d: d.facet == facet
                           and d.kind in ("value-mismatch", "missing"))


def _award_red(v: ledgerverify.LedgerVerdict) -> bool:
    return _hard(v, lambda d: d.kind == "unexpected-award")


def _roster_red(name: str) -> Callable[[ledgerverify.LedgerVerdict], bool]:
    return lambda v: _hard(v, lambda d: d.facet == "roster" and d.identity == name)


def _parse_error_red(v: ledgerverify.LedgerVerdict) -> bool:
    return _hard(v, lambda d: d.kind == "manifest-parse-error")


def _tooling_red(v: ledgerverify.LedgerVerdict) -> bool:
    return v.tooling and v.result.get("status") == oracle.ORACLE_STATUS_INVALID


def _first_reasons(v: ledgerverify.LedgerVerdict) -> List[str]:
    if v.tooling:
        return ["ledgerOracle INVALID(tooling): %s" % v.result.get("reason", "")]
    hard = ["ledgerOracle %s: %s" % (d.facet, d.detail) for d in v.divergences if d.hard]
    return hard or ["ledgerOracle status %s" % v.result.get("status")]


# ---------------------------------------------------------------------------
# Log reduction (the stock-award capture reads a handful of lines of a large log).
# ---------------------------------------------------------------------------


def reduce_log(text: str) -> str:
    """The log with every line the stock-award capture cannot read blanked: kept
    are the UT-stamped ``[Parsek]`` lines (the capture's UT source), the award lines
    and the balance lines it rejects. Line ordinals are preserved (a null-UT award's
    seqKey is its line index)."""
    out: List[str] = []
    for line in (text or "").splitlines():
        if (hlib._STOCK_UT_RE.search(line)
                or any(p.regex.search(line) for p in hlib.STOCK_AWARD_PATTERNS)
                or any(bp.search(line) for bp in hlib.BALANCE_LINE_PATTERNS)):
            out.append(line)
        else:
            out.append("")
    return "\n".join(out) + "\n"


# ---------------------------------------------------------------------------
# Baseline.
# ---------------------------------------------------------------------------


@dataclass
class LedgerBaseline:
    seed_block: Optional[Dict]
    career_block: Optional[Dict]
    analysis_obj: Optional[Dict]
    analysis_text: Optional[str]
    log_text: str
    verdict: Optional[ledgerverify.LedgerVerdict]
    reasons: List[str]
    reduced: bool = False
    note: str = ""


def prepare_baseline(expectations: Dict, inputs: LedgerInputs,
                     reducer: Callable[[str], str] = reduce_log) -> LedgerBaseline:
    """Parse the archived inputs, check the verifier passes on them, and pick the
    log the edits run over: the reduced log when it captures exactly what the full
    log captures and the verdict is identical, else the full log."""
    led, _world = blocks(expectations)
    if inputs.read_error:
        return LedgerBaseline(None, None, None, None, inputs.log_text, None,
                              ["unreadable archive: %s" % inputs.read_error])
    if inputs.analysis_text is None:
        return LedgerBaseline(None, None, None, None, inputs.log_text, None,
                              ["no archived .analysis.json (the produced careerSave block)"])
    if led is not None and inputs.seed_block is None:
        return LedgerBaseline(None, None, None, inputs.analysis_text, inputs.log_text, None,
                              ["no archived seed (<runId>.manifest.json)"])
    career = hlib.parse_career_save_block(inputs.analysis_text)
    try:
        obj = json.loads(inputs.analysis_text)
    except (ValueError, TypeError):
        obj = None
    full = evaluate(expectations, inputs.seed_block, career, inputs.log_text)
    if not _passed(full):
        return LedgerBaseline(inputs.seed_block, career, obj, inputs.analysis_text,
                              inputs.log_text, full, _first_reasons(full))
    reduced_text = reducer(inputs.log_text)
    same_capture = (hlib.parse_stock_award_lines(reduced_text)
                    == hlib.parse_stock_award_lines(inputs.log_text))
    red = evaluate(expectations, inputs.seed_block, career, reduced_text)
    if same_capture and red.result == full.result:
        return LedgerBaseline(inputs.seed_block, career, obj, inputs.analysis_text,
                              reduced_text, red, [], True)
    return LedgerBaseline(inputs.seed_block, career, obj, inputs.analysis_text,
                          inputs.log_text, full, [], False,
                          "the reduced log captured differently; edits ran over the full log")


# ---------------------------------------------------------------------------
# Gate bookkeeping.
# ---------------------------------------------------------------------------


@dataclass
class LedgerCheck:
    mutations: list = field(default_factory=list)       # mutlib.Mutation
    gates: List[GateVerdict] = field(default_factory=list)
    notes: List[str] = field(default_factory=list)


@dataclass
class _Attempt:
    side: str
    detail: str
    killed: bool
    note: str


def _fmt(x) -> str:
    return "None" if x is None else ("%r" % float(x))


class _Recorder:
    def __init__(self, check: LedgerCheck) -> None:
        self.check = check

    def mutation(self, target: str, kind: str, detail: str, killed: bool, note: str) -> None:
        import mutlib  # local: mutlib imports this module inside check_ledger_lane
        self.check.mutations.append(mutlib.Mutation(
            VERIFIER, target, kind, detail, mutlib.KILLED if killed else mutlib.SURVIVED,
            "" if killed else mutlib.TRIAGE, "" if killed else note))

    def gate(self, label: str, block: str, window: str, measured: str,
             attempts: Sequence[_Attempt], unchecked: Sequence[str] = ()) -> None:
        tried = list(attempts)
        if not tried:
            self.check.gates.append(GateVerdict(label, block, window, measured, UNCHECKED,
                                                "; ".join(unchecked) or "no edit constructible"))
            return
        survivors = [a for a in tried if not a.killed]
        if not survivors:
            self.check.gates.append(GateVerdict(label, block, window, measured, PROVEN,
                                                "; ".join(unchecked)))
            return
        self.check.gates.append(GateVerdict(
            label, block, window, measured, VACUOUS,
            "; ".join("%s: %s" % (a.side, a.note) for a in survivors)))


# ---------------------------------------------------------------------------
# The edits.
# ---------------------------------------------------------------------------


def _expected(expectations: Dict, seed_block: Dict, log_text: str) -> Optional[oracle.ExpectedCareer]:
    """The oracle's expected career for planning only (the verdict never reads it)."""
    led, _w = blocks(expectations)
    if led is None or seed_block is None:
        return None
    try:
        seed = oracle.parse_seed_baseline(seed_block)
    except ValueError:
        return None
    seam, _cap, _err, _m = ledgerverify.build_manifest(led, log_text, seed, "")
    rec3 = bool(led.get("rec3CarveOut", False))
    wl = (led.get("rec3Whitelist", []) or []) if rec3 else []
    return oracle.compute_expected(seed, seam, oracle.default_tolerances(), wl)


def _pool_value(exp: Optional[oracle.ExpectedCareer], facet: str) -> Optional[float]:
    if exp is None:
        return None
    return {"funds": exp.funds, "sciencePool": exp.science, "reputation": exp.reputation}[facet]


def _parsed_value(career: Dict, has_key: str, value_key: str) -> Optional[float]:
    if not career.get(has_key):
        return None
    v = career.get(value_key)
    if isinstance(v, bool) or not isinstance(v, (int, float)):
        return None
    return float(v)


def _with_ledger(expectations: Dict, ledger_block: Dict) -> Dict:
    out = dict(expectations)
    out["ledger"] = ledger_block
    return out


def _plan_and_run(expectations: Dict, base: LedgerBaseline, facet: str, tol: float,
                  parsed: float, sign: int,
                  build: Callable[[float], Optional[Tuple[Dict, Dict, str]]],
                  kill: Callable[[ledgerverify.LedgerVerdict], bool]
                  ) -> Tuple[Optional[_Attempt], str]:
    """Scale an expected-side shift until the PLANNED expected value crosses the
    tolerance on ``sign``'s side of ``parsed``, then run the real verifier.

    ``build(shift)`` returns ``(expectations, seed_block, detail)`` with the edit
    applied, or None when the shift is not constructible (a capped author
    constant). Returns ``(attempt, unchecked_reason)``: attempt None when nothing
    was constructible; a survivor's note names whether the plan moved at all."""
    base_exp = _pool_value(_expected(expectations, base.seed_block, base.log_text), facet)
    step = tol * (1.0 + CROSSING_MARGIN)
    chosen = None
    moved = False
    for k in range(MAX_SCALE_STEPS + 1):
        built = build(sign * step * (2 ** k) + (parsed - base_exp))
        if built is None:
            break
        planned = _pool_value(_expected(built[0], built[1], base.log_text), facet)
        if planned is not None and base_exp is not None and planned != base_exp:
            moved = True
        chosen = (built, planned)
        if planned is not None and sign * (planned - parsed) > tol:
            break
    if chosen is None:
        return None, "no edit constructible"
    (exps, seed_block, detail), planned = chosen
    crossed = planned is not None and sign * (planned - parsed) > tol
    if not crossed and moved:
        return None, ("the planned expected value moved but never crossed within %d "
                      "doublings" % MAX_SCALE_STEPS)
    v = evaluate(exps, seed_block, base.career_block, base.log_text)
    killed = kill(v)
    if crossed:
        note = "the expected value crossed (planned %s vs parsed %s) and the verifier stayed green" % (
            _fmt(planned), _fmt(parsed))
    else:
        note = "dead path: the edit did not move the expected value (%s)" % _fmt(base_exp)
    return _Attempt("down" if sign < 0 else "up",
                    "%s; planned expected %s, parsed %s" % (detail, _fmt(planned), _fmt(parsed)),
                    killed, note), ""


def mutate_ledger(expectations: Dict, base: LedgerBaseline) -> LedgerCheck:
    """Every ledger-oracle edit for one spec over a baseline whose verdict is PASS.
    ``base`` is never modified."""
    check = LedgerCheck()
    rec = _Recorder(check)
    led, world = blocks(expectations)
    tol = oracle.default_tolerances()
    career = base.career_block or {}
    log_text = base.log_text

    def run(seed_block, career_block, text=None, exps=None) -> ledgerverify.LedgerVerdict:
        return evaluate(exps if exps is not None else expectations, seed_block,
                        career_block, log_text if text is None else text)

    def attempt(label: str, kind: str, side: str, detail: str,
                v: ledgerverify.LedgerVerdict, kill, note: str) -> _Attempt:
        killed = kill(v)
        rec.mutation(label, kind, detail, killed, note)
        return _Attempt(side, detail, killed, note)

    exp = _expected(expectations, base.seed_block, log_text) if led is not None else None

    # 1. Pools: the produced value and the seed value, each across both sides.
    if led is not None:
        absent = []
        for facet, has_key, value_key, tol_attr, _entry_key in POOLS:
            t = getattr(tol, tol_attr)
            e = _pool_value(exp, facet)
            p = _parsed_value(career, has_key, value_key)
            window = "tol %s" % _fmt(t)
            measured = "expected=%s parsed=%s" % (_fmt(e), _fmt(p))
            if e is None:
                absent.append(facet)
                continue
            # Produced side.
            tries: List[_Attempt] = []
            for sign in (1, -1):
                target = e + sign * t * (1.0 + CROSSING_MARGIN)
                cb = copy.deepcopy(career)
                cb[value_key] = target
                detail = "produced %s %s -> %s (expected %s)" % (facet, _fmt(p), _fmt(target), _fmt(e))
                tries.append(attempt("%s:produced" % facet, KIND_POOL,
                                     "down" if sign < 0 else "up", detail,
                                     run(base.seed_block, cb), _pool_red(facet),
                                     "the produced %s crossed the tolerance and the verifier "
                                     "stayed green" % facet))
            rec.gate("ledger.%s:produced" % facet, "ledger", window, measured, tries)
            # Seed side (the expected value moves).
            tries = []
            unchecked: List[str] = []
            seed_val = float(base.seed_block.get(value_key))

            def build_seed(shift, value_key=value_key, seed_val=seed_val, facet=facet):
                sb = dict(base.seed_block)
                sb[value_key] = seed_val + shift
                return expectations, sb, "seed %s %s -> %s" % (facet, _fmt(seed_val), _fmt(seed_val + shift))

            for sign in (1, -1):
                a, why = _plan_and_run(expectations, base, facet, t, p, sign, build_seed,
                                       _pool_red(facet))
                if a is None:
                    unchecked.append("%s: %s" % ("down" if sign < 0 else "up", why))
                    continue
                rec.mutation("%s:seed" % facet, KIND_SEED, a.detail, a.killed, a.note)
                tries.append(a)
            rec.gate("ledger.%s:seed" % facet, "ledger", window, measured, tries, unchecked)
            # Fault: the pool is missing from the produced save.
            cb = copy.deepcopy(career)
            cb[has_key] = False
            cb.pop(value_key, None)
            fa = attempt("ledger:drop-%s" % facet, KIND_FAULT, "fault",
                         "removed %s/%s from the produced careerSave" % (has_key, value_key),
                         run(base.seed_block, cb), _pool_red(facet),
                         "the produced save lost its %s pool and the verifier stayed green" % facet)
            rec.gate("ledger:drop-%s" % facet, "ledger", "fault", "", [fa])
        if absent:
            check.notes.append("pools absent from the seed (the oracle skips them by design): %s"
                               % ", ".join(absent))

    # 2. Manifest entries: each declared amount moved across, each non-zero entry removed.
    if led is not None:
        raw_manifest = list(led.get("manifest", []) or [])
        for i, raw in enumerate(raw_manifest):
            if not isinstance(raw, dict):
                continue
            kind = raw.get("kind")
            for entry_key in ("funds", "science", "reputation"):
                amount = raw.get(entry_key)
                if isinstance(amount, bool) or not isinstance(amount, (int, float)):
                    continue
                facet, has_key, value_key, tol_attr, _k = ENTRY_KEY_TO_POOL[entry_key]
                t = getattr(tol, tol_attr)
                e = _pool_value(exp, facet)
                p = _parsed_value(career, has_key, value_key)
                label = "manifest[%d].%s" % (i, entry_key)
                measured = "kind=%s amount=%s" % (kind, _fmt(amount))
                if e is None or p is None:
                    rec.gate(label, "ledger.manifest", "tol %s" % _fmt(t), measured, [],
                             ["the %s pool is absent from the seed or the produced save" % facet])
                    continue

                def build_entry(shift, i=i, entry_key=entry_key, amount=amount):
                    new_amount = float(amount) + shift
                    if entry_key == "reputation" and abs(new_amount) > oracle.MAX_REP_AUTHOR_CONSTANT:
                        return None
                    lb = copy.deepcopy(led)
                    lb["manifest"][i][entry_key] = new_amount
                    return (_with_ledger(expectations, lb), base.seed_block,
                            "manifest[%d] %s %s -> %s" % (i, entry_key, _fmt(amount), _fmt(new_amount)))

                tries = []
                unchecked = []
                for sign in (1, -1):
                    a, why = _plan_and_run(expectations, base, facet, t, p, sign, build_entry,
                                           _pool_red(facet))
                    if a is None:
                        unchecked.append("%s: %s" % ("down" if sign < 0 else "up", why))
                        continue
                    rec.mutation(label, KIND_ENTRY, a.detail, a.killed, a.note)
                    tries.append(a)
                rec.gate(label, "ledger.manifest", "tol %s" % _fmt(t), measured, tries, unchecked)
            # Removal (the declared award vanished from the manifest).
            nonzero = [k for k in ("funds", "science", "reputation")
                       if isinstance(raw.get(k), (int, float)) and not isinstance(raw.get(k), bool)
                       and float(raw.get(k)) != 0.0]
            if nonzero:
                lb = copy.deepcopy(led)
                del lb["manifest"][i]
                pools = [ENTRY_KEY_TO_POOL[k][0] for k in nonzero]

                def removed_red(v, pools=pools):
                    return any(_pool_red(f)(v) for f in pools) or _award_red(v)

                ra = attempt("manifest[%d]:removed" % i, KIND_REMOVE, "remove",
                             "removed manifest[%d] (kind=%s %s)" % (
                                 i, kind, " ".join("%s=%s" % (k, _fmt(raw[k])) for k in nonzero)),
                             run(base.seed_block, career, exps=_with_ledger(expectations, lb)),
                             removed_red,
                             "the declared award was removed and no pool or cross-check red")
                rec.gate("manifest[%d]:removed" % i, "ledger.manifest", "remove",
                         "kind=%s" % kind, [ra])

    # 3. The stock-award cross-check, where a spec arms it.
    if led is not None and hlib.capture_cross_check_gates(led):
        tries = []
        rep_amounts = [float(r.get("reputation")) for r in (led.get("manifest") or [])
                       if isinstance(r, dict) and isinstance(r.get("reputation"), (int, float))]
        inj = 7.25
        while any(abs(inj - a) <= 10 * tol.reputation for a in rep_amounts):
            inj += 10.0
        line = "[LOG 00:00:00.000] Added %s (%d) reputation: '%s'." % (repr(inj), round(inj), INJECTED_REASON)
        tries.append(attempt("captureCrossCheck", KIND_CAPTURE, "inject",
                             "appended a stock award line no entry explains: %s" % line,
                             run(base.seed_block, career, text=log_text + line + "\n"),
                             _award_red, "an unexplained stock award was injected and the "
                                         "cross-check stayed green"))
        cap = hlib.dedupe_captured_awards(hlib.parse_stock_award_lines(log_text).captured)
        lines = log_text.splitlines()
        for c in cap:
            if c.facet != "reputation" or not (0 <= c.seq < len(lines)):
                continue
            for sign in (1, -1):
                new_amount = c.amount + sign * tol.reputation * (1.0 + CROSSING_MARGIN)
                edited = list(lines)
                m = hlib.STOCK_AWARD_PATTERNS[0].regex.search(edited[c.seq])
                if m is None:
                    continue
                s, e_ = m.span("amount")
                edited[c.seq] = edited[c.seq][:s] + repr(new_amount) + edited[c.seq][e_:]
                tries.append(attempt(
                    "captureCrossCheck", KIND_CAPTURE, "%s line %d" % ("down" if sign < 0 else "up", c.seq),
                    "captured award line %d amount %s -> %s" % (c.seq, _fmt(c.amount), _fmt(new_amount)),
                    run(base.seed_block, career, text="\n".join(edited) + "\n"), _award_red,
                    "a captured award moved past the tolerance and the cross-check stayed green"))
        rec.gate("ledger.captureCrossCheck", "ledger", "gate",
                 "captured=%d" % len(cap), tries)
    elif led is not None and led.get(hlib.LEDGER_CAPTURE_CROSS_CHECK_KEY) is not None:
        check.notes.append("captureCrossCheck=%s: report-only, not a gate"
                           % led.get(hlib.LEDGER_CAPTURE_CROSS_CHECK_KEY))

    # 4. World: roster claims (vessel resources are not modelled).
    if world is not None:
        roster_block = world.get("roster") or {}
        present = [str(n) for n in (roster_block.get("present") or ()) if str(n)]
        absent_names = [str(n) for n in (roster_block.get("absent") or ()) if str(n)]
        roster = list(career.get("roster") or [])
        for name in present:
            cb = copy.deepcopy(career)
            cb["roster"] = [k for k in roster if str((k or {}).get("name", "")) != name]
            a = attempt("world.roster.present", KIND_ROSTER, "drop",
                        "removed %s from the produced roster" % name,
                        run(base.seed_block, cb), _roster_red(name),
                        "a kerbal declared present was removed and the roster stayed green")
            rec.gate("world.roster.present[%s]" % name, "world.roster", "present", "", [a])
        for name in absent_names:
            cb = copy.deepcopy(career)
            cb["roster"] = roster + [dict(INJECTED_KERBAL, name=name)]
            a = attempt("world.roster.absent", KIND_ROSTER, "add",
                        "added %s to the produced roster" % name,
                        run(base.seed_block, cb), _roster_red(name),
                        "a kerbal declared absent was added and the roster stayed green")
            rec.gate("world.roster.absent[%s]" % name, "world.roster", "absent", "", [a])
        if present or absent_names:
            cb = copy.deepcopy(career)
            cb["hasRoster"] = False
            cb.pop("roster", None)
            a = attempt("ledger:drop-roster", KIND_FAULT, "fault",
                        "removed hasRoster/roster from the produced careerSave",
                        run(base.seed_block, cb), _tooling_red,
                        "a declared roster claim met a save with no roster facet and did not "
                        "route to INVALID(tooling)")
            rec.gate("ledger:drop-roster", "world.roster", "fault", "", [a])
        if ledgerverify.world_declared_vessels(world):
            rec.gate("world.vessels", "world.vessels", "declared", "", [],
                     ["vessel resource edits are not modelled (no committed spec declares one)"])

    # 5. Lane faults.
    faults: List[Tuple[str, str, Callable[[], ledgerverify.LedgerVerdict], Callable, str]] = []
    text = base.analysis_text or ""
    faults.append(("tear", "truncated the .analysis.json text to half",
                   lambda: run(base.seed_block, hlib.parse_career_save_block(text[:len(text) // 2])),
                   _tooling_red, "a torn analysis file did not route to INVALID(tooling)"))
    if isinstance(base.analysis_obj, dict):
        dropped = {k: v for k, v in base.analysis_obj.items() if k != "careerSave"}
        faults.append(("drop-career-block", "removed the careerSave block from the analysis",
                       lambda: run(base.seed_block, hlib.parse_career_save_block(json.dumps(dropped))),
                       _tooling_red, "an analysis with no careerSave block did not route to "
                                     "INVALID(tooling)"))
    faults.append(("unparsed", "set careerSave parsed=false",
                   lambda: run(base.seed_block, dict(career, parsed=False)),
                   _tooling_red, "a parsed=false careerSave did not route to INVALID(tooling)"))
    if led is not None:
        faults.append(("drop-seed", "removed the seed baseline",
                       lambda: run(None, career), _tooling_red,
                       "a ledger check with no seed did not route to INVALID(tooling)"))
        bad = copy.deepcopy(led)
        bad["manifest"] = list(bad.get("manifest", []) or []) + [{"kind": UNKNOWN_KIND}]
        faults.append(("bad-manifest-entry", "appended a manifest entry of unknown kind",
                       lambda: run(base.seed_block, career, exps=_with_ledger(expectations, bad)),
                       _parse_error_red, "a rejected manifest entry did not red as a dropped "
                                         "expected effect"))
    for name, detail, go, kill, note in faults:
        a = attempt("ledger:%s" % name, KIND_FAULT, "fault", detail, go(), kill, note)
        rec.gate("ledger:%s" % name, "ledger", "fault", "", [a])
    return check
